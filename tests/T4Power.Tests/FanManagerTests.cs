using T4Power.Core;
using T4Power.Core.Fans;
using T4Power.Core.Model;

namespace T4Power.Tests;

/// <summary>
/// The write policy: what actually reaches the chip, and - more importantly - what does not.
/// A fake stands in for the SuperIO, which is the whole reason <see cref="IFanHardware"/> lives
/// in Core while the library that implements it lives in the executable.
/// </summary>
public class FanManagerTests
{
    sealed class FakeFanHardware : IFanHardware
    {
        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason => IsAvailable ? null : "fake is unavailable";
        public string? UnavailableHelpUrl => IsAvailable ? null : "https://example.invalid/driver";

        public List<FanChannel> ChannelList { get; } = [];
        public IReadOnlyList<FanChannel> Channels => ChannelList;

        public Dictionary<string, FanReading> Readings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Identifier, double Percent)> Writes { get; } = [];
        public List<string> Releases { get; } = [];

        public bool ThrowOnRefresh { get; set; }
        public int ReopenAttempts { get; private set; }
        public bool ReopenSucceeds { get; set; }

        public void Refresh()
        {
            if (ThrowOnRefresh) throw new FanHardwareException("the fake refused to refresh");
        }

        public FanReading? Read(string identifier) =>
            Readings.TryGetValue(identifier, out var reading) ? reading : null;

        public void SetPercent(string identifier, double percent)
        {
            Writes.Add((identifier, percent));
            Readings[identifier] = new FanReading
            {
                Percent = percent,
                Rpm = (int)(percent * 30),
                Mode = FanControlMode.Software,
            };
        }

        public void ReleaseToDefault(string identifier)
        {
            Releases.Add(identifier);
            Readings[identifier] = new FanReading { Percent = null, Rpm = 900, Mode = FanControlMode.Default };
        }

        public bool TryReopen()
        {
            ReopenAttempts++;
            return ReopenSucceeds;
        }

        public void Dispose() { }
    }

    const string Id = "/lpc/nct6701d/control/3";
    const string SourceUuid = "GPU-test-0001";

    static FakeFanHardware Hardware(double min = 0, double max = 100)
    {
        var fake = new FakeFanHardware();
        fake.ChannelList.Add(new FanChannel
        {
            Identifier = Id,
            Name = "Fan #4",
            ChipName = "Nuvoton NCT6701D",
            Index = 3,
            MinSoftwarePercent = min,
            MaxSoftwarePercent = max,
            RpmSensorIdentifier = "/lpc/nct6701d/fan/3",
        });
        return fake;
    }

    static FileLog Log() => new(Path.Combine(Path.GetTempPath(), $"t4power-test-{Guid.NewGuid():N}.log"));

    static AppConfig Config(double? overridePercent = null, bool managed = true) => new()
    {
        Fans =
        [
            new FanConfig
            {
                ControlIdentifier = Id,
                SourceGpuUuid = SourceUuid,
                Managed = managed,
                ChipName = "Nuvoton NCT6701D",
                ControlIndex = 3,
                Curve = FanCurve.DefaultForT4(),
                Override = overridePercent is { } p ? new FanOverride { Percent = p } : null,
            },
        ],
    };

    static Func<string, GpuTelemetry?> Telemetry(uint? temp = 50) => _ => temp is null
        ? null
        : new GpuTelemetry { Uuid = SourceUuid, TimestampUtc = DateTimeOffset.UtcNow, TemperatureC = temp };

    static void Ignore(FanConfig _) { }

    // ---- the deadband -----------------------------------------------------------------

    [Fact]
    public void A_change_below_the_deadband_is_not_written_to_the_chip()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);
        Assert.Single(fake.Writes);

        // 0.4% is not worth a port write, and at 1 Hz forever it is a lot of them.
        manager.Apply(Config(overridePercent: 50.4), Telemetry(), Ignore);
        Assert.Single(fake.Writes);

        manager.Apply(Config(overridePercent: 52), Telemetry(), Ignore);
        Assert.Equal(2, fake.Writes.Count);
        Assert.Equal(52, fake.Writes[^1].Percent);
    }

    [Fact]
    public void Losing_software_control_forces_a_rewrite_even_inside_the_deadband()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);
        Assert.Single(fake.Writes);

        // Something else - the board's own service, a leftover fan app - took the channel back.
        fake.Readings[Id] = new FanReading { Percent = 30, Rpm = 900, Mode = FanControlMode.Default };

        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);
        Assert.Equal(2, fake.Writes.Count);
    }

    // ---- clamping ---------------------------------------------------------------------

    [Fact]
    public void Commanded_duty_is_clamped_to_what_the_chip_accepts()
    {
        // The security-relevant one: these values arrive over the pipe, and untrusted input must
        // never reach the SuperIO unbounded.
        var fake = Hardware(min: 30, max: 80);
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 100), Telemetry(), Ignore);
        Assert.Equal(80, fake.Writes[^1].Percent);

        manager.Apply(Config(overridePercent: 5), Telemetry(), Ignore);
        Assert.Equal(30, fake.Writes[^1].Percent);
    }

    // ---- what must never be written ----------------------------------------------------

    [Fact]
    public void An_unmanaged_header_is_never_written_to_at_all()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 50, managed: false), Telemetry(), Ignore);

        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Releases);
    }

    [Fact]
    public void A_configured_header_that_is_no_longer_present_is_left_alone()
    {
        var fake = new FakeFanHardware();   // no channels at all
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);

        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Releases);

        var status = manager.Statuses.Single();
        Assert.False(status.Present);
    }

    [Fact]
    public void Losing_the_source_gpu_hands_the_header_back_to_the_bios()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(), Telemetry(), Ignore);
        Assert.Single(fake.Writes);

        manager.Apply(Config(), Telemetry(temp: null), Ignore);
        Assert.Equal([Id], fake.Releases);

        // And the release is not repeated every tick once it has happened.
        manager.Apply(Config(), Telemetry(temp: null), Ignore);
        Assert.Single(fake.Releases);
    }

    // ---- failure handling ---------------------------------------------------------------

    [Fact]
    public void Hardware_failures_never_propagate_and_eventually_disable_fan_control()
    {
        var fake = Hardware();
        fake.ThrowOnRefresh = true;
        fake.ReopenSucceeds = false;
        using var manager = new FanManager(fake, Log());

        // A fan that cannot be read must not take GPU management down with it.
        for (var i = 0; i < 10; i++)
            manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);

        Assert.Equal(1, fake.ReopenAttempts);      // one attempt, not one per tick
        Assert.False(manager.IsAvailable);
        Assert.Contains("restart", manager.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_successful_reopen_puts_fan_control_back_to_work()
    {
        var fake = Hardware();
        fake.ThrowOnRefresh = true;
        fake.ReopenSucceeds = true;
        using var manager = new FanManager(fake, Log());

        for (var i = 0; i < 5; i++)
            manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);

        Assert.Equal(1, fake.ReopenAttempts);
        Assert.True(manager.IsAvailable);

        fake.ThrowOnRefresh = false;
        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);
        Assert.Single(fake.Writes);
    }

    // ---- reporting why it is unavailable --------------------------------------------------

    [Fact]
    public void An_unavailable_backend_surfaces_both_its_reason_and_somewhere_to_go()
    {
        var fake = Hardware();
        fake.IsAvailable = false;
        using var manager = new FanManager(fake, Log());

        Assert.False(manager.IsAvailable);
        Assert.Equal("fake is unavailable", manager.UnavailableReason);
        Assert.Equal("https://example.invalid/driver", manager.UnavailableHelpUrl);
    }

    [Fact]
    public void Giving_up_reports_a_restart_rather_than_a_download()
    {
        // Once we have given up, the hardware layer opened fine and the fix is a service restart.
        // Pointing the user at an installer for a driver they already have would be misleading.
        var fake = Hardware();
        fake.ThrowOnRefresh = true;
        fake.ReopenSucceeds = false;
        using var manager = new FanManager(fake, Log());

        for (var i = 0; i < 6; i++)
            manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);

        Assert.False(manager.IsAvailable);
        Assert.Contains("restart", manager.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(manager.UnavailableHelpUrl);
    }

    // ---- shutdown ------------------------------------------------------------------------

    [Fact]
    public void ReleaseAll_hands_back_every_header_we_ever_took()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);
        manager.ReleaseAll();

        Assert.Equal([Id], fake.Releases);
    }

    [Fact]
    public void ReleaseAll_does_not_touch_headers_we_never_drove()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.ReleaseAll();

        Assert.Empty(fake.Releases);
    }

    // ---- re-binding ------------------------------------------------------------------------

    [Fact]
    public void A_moved_identifier_is_rebound_by_chip_and_index_and_persisted()
    {
        var fake = new FakeFanHardware();
        fake.ChannelList.Add(new FanChannel
        {
            // Same chip, same index, different identifier - what a library rename looks like.
            Identifier = "/lpc/nct6701/control/3",
            Name = "Fan #4",
            ChipName = "Nuvoton NCT6701D",
            Index = 3,
            RpmSensorIdentifier = "/lpc/nct6701/fan/3",
        });

        using var manager = new FanManager(fake, Log());
        FanConfig? persisted = null;

        manager.Apply(Config(overridePercent: 50), Telemetry(), f => persisted = f);

        Assert.Equal("/lpc/nct6701/control/3", persisted?.ControlIdentifier);
        Assert.Equal("/lpc/nct6701/control/3", fake.Writes.Single().Identifier);
    }

    [Fact]
    public void A_header_on_a_different_chip_is_never_rebound_to()
    {
        var fake = new FakeFanHardware();
        fake.ChannelList.Add(new FanChannel
        {
            Identifier = "/lpc/it8688e/control/3",
            Name = "Fan #4",
            ChipName = "ITE IT8688E",   // different board entirely
            Index = 3,
        });

        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 50), Telemetry(), Ignore);

        // Driving the wrong header is much worse than driving none.
        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Releases);
    }

    // ---- adoption verification ---------------------------------------------------------------

    [Fact]
    public void Verification_passes_when_the_tachometer_responds()
    {
        var fake = Hardware();
        fake.Readings[Id] = new FanReading { Percent = 20, Rpm = 600, Mode = FanControlMode.Default };
        using var manager = new FanManager(fake, Log());

        var problem = manager.Verify(fake.ChannelList[0], _ => { });

        Assert.Null(problem);
    }

    [Fact]
    public void Verification_fails_when_the_header_reports_no_rpm()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        // A header with nothing plugged into it: writes succeed, but the fake reports 0 RPM.
        fake.ChannelList[0] = fake.ChannelList[0] with { MaxSoftwarePercent = 0 };

        var problem = manager.Verify(fake.ChannelList[0], _ => { });

        Assert.NotNull(problem);
        Assert.Contains("RPM", problem);
    }

    [Fact]
    public void Verification_without_a_tachometer_is_reported_rather_than_assumed()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        var channel = fake.ChannelList[0] with { RpmSensorIdentifier = null };
        var problem = manager.Verify(channel, _ => { });

        Assert.NotNull(problem);
        Assert.Contains("tachometer", problem);
    }

    [Fact]
    public void Verification_restores_the_channel_afterwards()
    {
        var fake = Hardware();
        using var manager = new FanManager(fake, Log());

        manager.Apply(Config(overridePercent: 40), Telemetry(), Ignore);
        manager.Verify(fake.ChannelList[0], _ => { });

        // Whatever verification did, the header must end up back where it was.
        Assert.Equal(40, fake.Writes[^1].Percent);
    }
}
