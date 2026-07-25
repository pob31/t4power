using T4Power.Core.Model;
using T4Power.Core.Nvml;
using T4Power.Core.Rules;

namespace T4Power.Tests;

/// <summary>
/// The rule engine is deliberately pure - it reads config and telemetry and returns a decision
/// without touching NVML - so all of this runs without a GPU. Hysteresis is the part most
/// likely to be subtly wrong, so it gets the most attention here.
/// </summary>
public class RuleEngineTests
{
    // A controllable clock, since every hysteresis assertion is about the passage of time.
    sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public void Advance(int seconds) => Advance(TimeSpan.FromSeconds(seconds));
    }

    static GpuConfig Config(params Rule[] rules) => new()
    {
        Uuid = "GPU-test-0001",
        Managed = true,
        DefaultProfile = Profile.Eco,
        Profiles =
        [
            new Profile { Name = Profile.Eco, PowerLimitW = 60, LockClocks = new ClockLock { MinMhz = 300, MaxMhz = 900 } },
            new Profile { Name = Profile.Max, PowerLimitW = 70, LockClocks = null },
        ],
        Rules = rules.Length > 0 ? rules : Rule.Defaults(),
    };

    static GpuTelemetry Telemetry(uint util = 0, uint[]? pids = null, uint temp = 50) => new()
    {
        Uuid = "GPU-test-0001",
        TimestampUtc = DateTimeOffset.UtcNow,
        GpuUtilPercent = util,
        TemperatureC = temp,
        ActivePids = pids ?? [],
    };

    static IReadOnlySet<string> Running(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    static readonly IReadOnlySet<string> NothingRunning = new HashSet<string>();

    // ---- basic selection -------------------------------------------------------------

    [Fact]
    public void Idle_system_falls_back_to_Eco()
    {
        var engine = new RuleEngine();
        var d = engine.Evaluate(Config(), Telemetry(), NothingRunning);

        Assert.Equal(DecisionSource.Rule, d.Source);
        Assert.Equal(Profile.Eco, d.Desired?.ProfileName);
    }

    [Fact]
    public void Active_compute_context_ramps_to_Max_immediately()
    {
        var engine = new RuleEngine();
        var d = engine.Evaluate(Config(), Telemetry(pids: [1234]), NothingRunning);

        Assert.Equal(Profile.Max, d.Desired?.ProfileName);
    }

    [Fact]
    public void Utilisation_above_threshold_ramps_to_Max_even_with_no_visible_process()
    {
        var engine = new RuleEngine();
        var d = engine.Evaluate(Config(), Telemetry(util: 40), NothingRunning);

        Assert.Equal(Profile.Max, d.Desired?.ProfileName);
    }

    [Fact]
    public void Watched_process_ramps_to_Max_before_it_touches_the_gpu()
    {
        var rules = new[]
        {
            new Rule { Type = RuleType.ProcessName, Priority = 100, ProfileName = Profile.Max, Match = ["ollama.exe"], DwellSeconds = 60 },
            new Rule { Type = RuleType.Idle, Priority = 0, ProfileName = Profile.Eco },
        };

        var engine = new RuleEngine();
        // GPU completely idle - the ramp is driven purely by the process being present.
        var d = engine.Evaluate(Config(rules), Telemetry(), Running("ollama"));

        Assert.Equal(Profile.Max, d.Desired?.ProfileName);
    }

    [Theory]
    [InlineData("ollama.exe", "ollama")]
    [InlineData("ollama", "ollama")]
    [InlineData("Ollama.EXE", "OLLAMA")]
    public void Process_matching_ignores_case_and_exe_suffix(string configured, string actual)
    {
        var rules = new[]
        {
            new Rule { Type = RuleType.ProcessName, Priority = 100, ProfileName = Profile.Max, Match = [configured] },
            new Rule { Type = RuleType.Idle, Priority = 0, ProfileName = Profile.Eco },
        };

        var d = new RuleEngine().Evaluate(Config(rules), Telemetry(), Running(actual));
        Assert.Equal(Profile.Max, d.Desired?.ProfileName);
    }

    // ---- hysteresis ------------------------------------------------------------------

    [Fact]
    public void Ramp_up_is_immediate_but_ramp_down_waits_for_the_dwell_period()
    {
        var clock = new TestClock();
        var engine = new RuleEngine(() => clock.Now);
        var config = Config();   // GpuActivity dwell is 300s by default

        // Work starts: Max at once, no waiting.
        Assert.Equal(Profile.Max, engine.Evaluate(config, Telemetry(pids: [42]), NothingRunning).Desired?.ProfileName);

        // Work stops. The card must not drop instantly.
        clock.Advance(10);
        Assert.Equal(Profile.Max, engine.Evaluate(config, Telemetry(), NothingRunning).Desired?.ProfileName);

        // Still inside the dwell window.
        clock.Advance(280);
        Assert.Equal(Profile.Max, engine.Evaluate(config, Telemetry(), NothingRunning).Desired?.ProfileName);

        // Past it: now Eco is allowed.
        clock.Advance(30);
        Assert.Equal(Profile.Eco, engine.Evaluate(config, Telemetry(), NothingRunning).Desired?.ProfileName);
    }

    [Fact]
    public void Intermittent_work_does_not_flap_the_profile()
    {
        var clock = new TestClock();
        var engine = new RuleEngine(() => clock.Now);
        var config = Config();

        // A job that alternates between busy and idle every 30s - the gaps between kernels.
        // Across the whole run the profile must stay pinned at Max, never dipping to Eco.
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var busy = engine.Evaluate(config, Telemetry(pids: [7]), NothingRunning);
            Assert.Equal(Profile.Max, busy.Desired?.ProfileName);

            clock.Advance(30);
            var gap = engine.Evaluate(config, Telemetry(), NothingRunning);
            Assert.Equal(Profile.Max, gap.Desired?.ProfileName);

            clock.Advance(30);
        }
    }

    [Fact]
    public void Dwell_window_restarts_each_time_the_condition_becomes_true_again()
    {
        var clock = new TestClock();
        var engine = new RuleEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(pids: [1]), NothingRunning);

        clock.Advance(290);                                   // nearly expired...
        engine.Evaluate(config, Telemetry(pids: [1]), NothingRunning);  // ...but work resumes

        clock.Advance(290);                                   // would have expired on the old timer
        Assert.Equal(Profile.Max, engine.Evaluate(config, Telemetry(), NothingRunning).Desired?.ProfileName);

        clock.Advance(20);
        Assert.Equal(Profile.Eco, engine.Evaluate(config, Telemetry(), NothingRunning).Desired?.ProfileName);
    }

    [Fact]
    public void Zero_dwell_drops_immediately()
    {
        var rules = new[]
        {
            new Rule { Type = RuleType.GpuActivity, Priority = 50, ProfileName = Profile.Max, DwellSeconds = 0 },
            new Rule { Type = RuleType.Idle, Priority = 0, ProfileName = Profile.Eco },
        };
        var clock = new TestClock();
        var engine = new RuleEngine(() => clock.Now);
        var config = Config(rules);

        Assert.Equal(Profile.Max, engine.Evaluate(config, Telemetry(pids: [1]), NothingRunning).Desired?.ProfileName);
        clock.Advance(1);
        Assert.Equal(Profile.Eco, engine.Evaluate(config, Telemetry(), NothingRunning).Desired?.ProfileName);
    }

    // ---- priority --------------------------------------------------------------------

    [Fact]
    public void Higher_priority_rule_wins_when_several_match()
    {
        var rules = new[]
        {
            new Rule { Type = RuleType.ProcessName, Priority = 100, ProfileName = Profile.Eco, Match = ["throttled"] },
            new Rule { Type = RuleType.GpuActivity, Priority = 50, ProfileName = Profile.Max },
            new Rule { Type = RuleType.Idle, Priority = 0, ProfileName = Profile.Eco },
        };

        // Both the process rule and the activity rule match; the higher priority one wins even
        // though it asks for the lower power state.
        var d = new RuleEngine().Evaluate(Config(rules), Telemetry(pids: [1]), Running("throttled"));
        Assert.Equal(Profile.Eco, d.Desired?.ProfileName);
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var rules = new[]
        {
            new Rule { Type = RuleType.ProcessName, Priority = 100, ProfileName = Profile.Max, Match = ["x"], Enabled = false },
            new Rule { Type = RuleType.Idle, Priority = 0, ProfileName = Profile.Eco },
        };

        var d = new RuleEngine().Evaluate(Config(rules), Telemetry(), Running("x"));
        Assert.Equal(Profile.Eco, d.Desired?.ProfileName);
    }

    [Fact]
    public void Rule_naming_a_missing_profile_is_skipped_rather_than_crashing()
    {
        var rules = new[]
        {
            new Rule { Type = RuleType.GpuActivity, Priority = 50, ProfileName = "ProfileThatWasDeleted" },
            new Rule { Type = RuleType.Idle, Priority = 0, ProfileName = Profile.Eco },
        };

        var d = new RuleEngine().Evaluate(Config(rules), Telemetry(pids: [1]), NothingRunning);
        Assert.Equal(Profile.Eco, d.Desired?.ProfileName);
    }

    // ---- overrides -------------------------------------------------------------------

    [Fact]
    public void Override_beats_the_rules()
    {
        var config = Config() with { Override = new Override { ProfileName = Profile.Max } };

        // Nothing is running, so the rules would say Eco.
        var d = new RuleEngine().Evaluate(config, Telemetry(), NothingRunning);

        Assert.Equal(DecisionSource.Override, d.Source);
        Assert.Equal(Profile.Max, d.Desired?.ProfileName);
    }

    [Fact]
    public void Expired_override_hands_control_back_to_the_rules()
    {
        var clock = new TestClock();
        var engine = new RuleEngine(() => clock.Now);
        var config = Config() with
        {
            Override = new Override { ProfileName = Profile.Max, ExpiresUtc = clock.Now.AddMinutes(30) },
        };

        Assert.Equal(DecisionSource.Override, engine.Evaluate(config, Telemetry(), NothingRunning).Source);

        clock.Advance(TimeSpan.FromMinutes(31));
        var after = engine.Evaluate(config, Telemetry(), NothingRunning);

        Assert.Equal(DecisionSource.Rule, after.Source);
        Assert.Equal(Profile.Eco, after.Desired?.ProfileName);
    }

    [Fact]
    public void Ad_hoc_override_carries_raw_values_rather_than_a_profile()
    {
        var config = Config() with
        {
            Override = new Override
            {
                PowerLimitW = 65,
                LockClocks = new ClockLock { MinMhz = 300, MaxMhz = 1200 },
            },
        };

        var d = new RuleEngine().Evaluate(config, Telemetry(), NothingRunning);

        Assert.Equal(65, d.Desired?.PowerLimitW);
        Assert.Equal(1200u, d.Desired?.LockClocks?.MaxMhz);
    }

    // ---- safety ----------------------------------------------------------------------

    [Fact]
    public void Thermal_guard_outranks_even_a_manual_override()
    {
        var config = Config() with
        {
            ThermalGuardC = 85,
            Override = new Override { ProfileName = Profile.Max },
        };

        var d = new RuleEngine().Evaluate(config, Telemetry(pids: [1], temp: 90), NothingRunning);

        Assert.Equal(DecisionSource.ThermalGuard, d.Source);
        Assert.Equal(Profile.Eco, d.Desired?.ProfileName);
        Assert.Contains("90", d.Reason);
    }

    [Fact]
    public void Thermal_guard_stays_out_of_the_way_below_the_limit()
    {
        var config = Config() with { ThermalGuardC = 85, Override = new Override { ProfileName = Profile.Max } };

        var d = new RuleEngine().Evaluate(config, Telemetry(temp: 84), NothingRunning);

        Assert.Equal(DecisionSource.Override, d.Source);
        Assert.Equal(Profile.Max, d.Desired?.ProfileName);
    }

    [Fact]
    public void Unmanaged_gpu_yields_no_desired_state_at_all()
    {
        var config = Config() with { Managed = false };

        var d = new RuleEngine().Evaluate(config, Telemetry(pids: [1]), NothingRunning);

        Assert.Equal(DecisionSource.Unmanaged, d.Source);
        Assert.Null(d.Desired);
    }

    [Fact]
    public void Hysteresis_state_is_tracked_per_gpu()
    {
        var clock = new TestClock();
        var engine = new RuleEngine(() => clock.Now);
        var a = Config() with { Uuid = "GPU-aaaa" };
        var b = Config() with { Uuid = "GPU-bbbb" };

        engine.Evaluate(a, Telemetry(pids: [1]), NothingRunning);   // only A saw work
        clock.Advance(5);

        Assert.Equal(Profile.Max, engine.Evaluate(a, Telemetry(), NothingRunning).Desired?.ProfileName);
        Assert.Equal(Profile.Eco, engine.Evaluate(b, Telemetry(), NothingRunning).Desired?.ProfileName);
    }
}

/// <summary>Clamping is a security property here, not just tidiness: the pipe is reachable by
/// unelevated callers, so out-of-range values must never reach NVML.</summary>
public class GpuInfoTests
{
    static readonly GpuInfo T4 = new()
    {
        Index = 1,
        Uuid = "GPU-fb0e16f4-561d-9314-9f1e-5b819256f1d8",
        Name = "Tesla T4",
        MinPowerLimitW = 60,
        MaxPowerLimitW = 70,
        DefaultPowerLimitW = 70,
        SupportsPowerLimit = true,
        SupportedGraphicsClocksMhz = Enumerable.Range(0, 87).Select(i => (uint)(1590 - i * 15)).ToArray(),
    };

    [Theory]
    [InlineData(200, 70)]     // absurdly high: clamped to the max the card reports
    [InlineData(0, 60)]       // absurdly low: clamped to the floor
    [InlineData(-50, 60)]
    [InlineData(65, 65)]      // in range: untouched
    public void Power_requests_are_clamped_to_the_reported_envelope(double requested, double expected) =>
        Assert.Equal(expected, T4.ClampPowerW(requested));

    [Theory]
    [InlineData(900, 900)]    // exact supported step
    [InlineData(905, 900)]    // snaps to nearest
    [InlineData(898, 900)]
    [InlineData(99999, 1590)] // above max
    [InlineData(1, 300)]      // below min
    public void Clock_requests_snap_to_a_supported_step(uint requested, uint expected) =>
        Assert.Equal(expected, T4.SnapClock(requested));

    [Fact]
    public void T4_is_recognised_so_it_gets_clock_aware_defaults()
    {
        Assert.True(T4.IsTeslaT4);
        Assert.Equal(300u, T4.MinGraphicsClockMhz);
        Assert.Equal(1590u, T4.MaxGraphicsClockMhz);
    }

    [Fact]
    public void Default_T4_profiles_lock_clocks_because_the_power_band_alone_is_only_10W()
    {
        var profiles = Profile.DefaultsForT4(T4);

        var eco = profiles.Single(p => p.Name == Profile.Eco);
        Assert.Equal(60, eco.PowerLimitW);
        Assert.NotNull(eco.LockClocks);           // the knob that actually lowers idle draw
        Assert.Equal(300u, eco.LockClocks!.MinMhz);
        Assert.Equal(900u, eco.LockClocks.MaxMhz);

        var max = profiles.Single(p => p.Name == Profile.Max);
        Assert.Equal(70, max.PowerLimitW);
        Assert.Null(max.LockClocks);              // unlocked: full 1590 MHz available
    }

    [Fact]
    public void Non_T4_gpus_get_power_only_profiles_and_are_unmanaged_by_default()
    {
        var consumer = T4 with { Name = "NVIDIA GeForce RTX 5070", Uuid = "GPU-0909", MinPowerLimitW = 175, MaxPowerLimitW = 275 };

        Assert.False(consumer.IsTeslaT4);
        // Locking clocks on a display GPU would hurt desktop responsiveness for no benefit.
        Assert.All(Profile.DefaultsForGeneric(consumer), p => Assert.Null(p.LockClocks));
        Assert.False(GpuConfig.CreateDefault(consumer).Managed);
    }
}

public class GpuSelectorTests
{
    static readonly GpuInfo Info = new()
    {
        Index = 1,
        Uuid = "GPU-fb0e16f4-561d-9314-9f1e-5b819256f1d8",
        Name = "Tesla T4",
        MinPowerLimitW = 60,
        MaxPowerLimitW = 70,
        DefaultPowerLimitW = 70,
        SupportsPowerLimit = true,
        SupportedGraphicsClocksMhz = [1590, 300],
    };

    [Theory]
    [InlineData("GPU-fb0e16f4-561d-9314-9f1e-5b819256f1d8")]  // full uuid
    [InlineData("gpu-FB0E16F4-561d-9314-9f1e-5b819256f1d8")]  // case-insensitive
    [InlineData("fb0e16f4")]                                   // uuid prefix
    [InlineData("1")]                                          // index
    [InlineData("T4")]                                         // name substring
    [InlineData("tesla")]
    [InlineData(null)]                                         // no selector means all
    public void Accepts_the_selector_forms_a_user_might_type(string? selector) =>
        Assert.True(GpuSelector.Matches(Info, selector));

    [Theory]
    [InlineData("GPU-090dcd21-5851-1641-59e8-da6786733725")]   // the other card
    [InlineData("0")]
    [InlineData("5070")]
    [InlineData("fb0e")]                                       // too short to be unambiguous
    public void Rejects_selectors_that_belong_to_another_gpu(string selector) =>
        Assert.False(GpuSelector.Matches(Info, selector));
}
