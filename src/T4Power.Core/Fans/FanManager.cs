using T4Power.Core.Model;

namespace T4Power.Core.Fans;

/// <summary>What a header is doing and why, for the UI, <c>--fans</c> and the IPC contract.</summary>
public sealed record FanStatus
{
    public required string ControlIdentifier { get; init; }
    public string? Name { get; init; }
    public string? ChipName { get; init; }
    public int Index { get; init; } = -1;
    public bool Managed { get; init; }
    public bool Verified { get; init; }
    public bool Present { get; init; }

    public string? SourceGpuUuid { get; init; }

    /// <summary>Filled in by <see cref="Control.GpuManager"/>, which is the only thing that knows
    /// GPU names — a bare UUID is no use to someone reading <c>--fans</c>.</summary>
    public string? SourceGpuName { get; init; }

    public uint? SourceTemperatureC { get; init; }

    public double? CommandedPercent { get; init; }
    public double? ReadbackPercent { get; init; }
    public int? Rpm { get; init; }
    public FanControlMode Mode { get; init; }

    public FanDecisionSource Source { get; init; }
    public string? Reason { get; init; }
    public FanOverride? Override { get; init; }
    public FanCurve? Curve { get; init; }
}

/// <summary>
/// Turns fan decisions into writes, and is the only thing in the system that commands a duty.
///
/// It takes no lock of its own: every entry point is called from inside
/// <see cref="Control.GpuManager"/>'s tick monitor, which is the single serialisation point for
/// everything that touches hardware. Adding a second lock here would buy nothing and invite the
/// deadlock that the one-lock design exists to avoid — so if you call into this class from
/// somewhere new, hold that monitor.
/// </summary>
public sealed class FanManager : IDisposable
{
    /// <summary>How far a target must move before it is worth another write to the chip.</summary>
    const double DeadbandPercent = 1.0;

    /// <summary>Consecutive hardware failures tolerated before giving up and reopening.</summary>
    const int FailureBudget = 5;

    readonly IFanHardware _hardware;
    readonly FanCurveEngine _engine;
    readonly FileLog _log;
    readonly Func<DateTimeOffset> _clock;

    /// <summary>Last duty commanded per channel, so an unchanged target is not rewritten at 1 Hz.</summary>
    readonly Dictionary<string, double> _applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Channels currently handed back to the BIOS, so the release is not repeated.</summary>
    readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Channels we have ever taken over, so shutdown can release exactly those.</summary>
    readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, FanStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rate limiter for the noisier warnings, which would otherwise fire every second.</summary>
    readonly Dictionary<string, DateTimeOffset> _lastComplaint = new(StringComparer.OrdinalIgnoreCase);

    int _failures;
    bool _givenUp;
    bool _disposed;

    public FanManager(IFanHardware hardware, FileLog log, Func<DateTimeOffset>? clock = null)
    {
        _hardware = hardware;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _engine = new FanCurveEngine(_clock);
    }

    public bool IsAvailable => _hardware.IsAvailable && !_givenUp;

    public string? UnavailableReason => _givenUp
        ? "fan control stopped responding and was disabled; restart the T4Power service to retry"
        : _hardware.UnavailableReason;

    public IReadOnlyList<FanChannel> Channels => _hardware.Channels;

    public IReadOnlyList<FanStatus> Statuses => _status.Values.ToList();

    /// <summary>
    /// Every header on the board with a live reading, adopted or not.
    ///
    /// Unadopted headers get a reading too, which is the point: watching which header's RPM
    /// changes is a far more reliable way to find the one you want than listening for it, and
    /// picking the wrong header is the mistake with real consequences here.
    /// </summary>
    public IReadOnlyList<FanStatus> Survey()
    {
        if (!IsAvailable) return [];

        try
        {
            _hardware.Refresh();
        }
        catch (Exception ex)
        {
            NoteFailure("refreshing fan sensors", ex);
        }

        return _hardware.Channels.Select(channel =>
        {
            if (_status.TryGetValue(channel.Identifier, out var adopted)) return adopted;

            var reading = TryRead(channel.Identifier);

            return new FanStatus
            {
                ControlIdentifier = channel.Identifier,
                Name = channel.Name,
                ChipName = channel.ChipName,
                Index = channel.Index,
                Managed = false,
                Present = true,
                ReadbackPercent = reading?.Percent,
                Rpm = reading?.Rpm,
                Mode = reading?.Mode ?? FanControlMode.Undefined,
                Source = FanDecisionSource.Unmanaged,
                Reason = "not managed by T4Power",
            };
        }).ToList();
    }

    // ---- the tick --------------------------------------------------------------------

    /// <summary>
    /// One pass over every configured header. Never throws: a fan that cannot be read must not
    /// stop the GPU being managed, which is the same reasoning behind the per-tick catch in the
    /// worker.
    /// </summary>
    public void Apply(AppConfig config, Func<string, GpuTelemetry?> telemetryFor, Action<FanConfig> persist)
    {
        if (_disposed || !IsAvailable) return;

        try
        {
            _hardware.Refresh();
        }
        catch (Exception ex)
        {
            NoteFailure("refreshing fan sensors", ex);
            return;
        }

        _status.Clear();

        foreach (var fan in config.Fans)
        {
            try
            {
                ApplyOne(fan, telemetryFor, persist);
            }
            catch (Exception ex)
            {
                NoteFailure($"driving {fan.DisplayName}", ex);
            }
        }
    }

    void ApplyOne(FanConfig fan, Func<string, GpuTelemetry?> telemetryFor, Action<FanConfig> persist)
    {
        var channel = Resolve(fan, persist);
        var telemetry = telemetryFor(fan.SourceGpuUuid);

        if (channel is null)
        {
            // Managed but missing. Do not guess at a replacement: driving the wrong header is far
            // worse than driving none, and the BIOS curve is still in charge of this one.
            Complain(fan.ControlIdentifier, TimeSpan.FromMinutes(10),
                $"fan header '{fan.ControlIdentifier}' is configured but not present; leaving it to the BIOS");

            _status[fan.ControlIdentifier] = StatusFor(fan, channel: null, reading: null, telemetry,
                FanDecision.None(FanDecisionSource.FailSafe, "this header is no longer present on the board"));
            return;
        }

        // Expire an override here rather than letting the engine re-derive it every tick, so the
        // change is actually persisted - the same reasoning as the GPU override expiry.
        if (fan.Override?.IsExpired(_clock()) == true)
        {
            _log.Info($"{fan.DisplayName}: fan override expired, returning to the curve");
            fan = fan with { Override = null };
            persist(fan);
        }

        var decision = _engine.Evaluate(fan, telemetry);
        var reading = TryRead(channel.Identifier);

        _status[fan.ControlIdentifier] = StatusFor(fan, channel, reading, telemetry, decision);

        switch (decision.Source)
        {
            case FanDecisionSource.Unmanaged:
                return;

            case FanDecisionSource.FailSafe when decision.ReleaseToDefault:
                Release(channel, decision.Reason);
                return;

            default:
                if (decision.Percent is { } percent) Command(channel, percent, decision.Reason);
                return;
        }
    }

    void Command(FanChannel channel, double percent, string reason)
    {
        // Clamp to what the chip says it accepts. Values reach here from the pipe, and untrusted
        // input must never be handed to the SuperIO unbounded - the same rule ClampPowerW applies
        // to watts.
        var wanted = Math.Clamp(percent, channel.MinSoftwarePercent, channel.MaxSoftwarePercent);

        var reading = TryRead(channel.Identifier);
        var lost = reading is not null && reading.Mode != FanControlMode.Software;
        var had = _applied.TryGetValue(channel.Identifier, out var last) ? last : (double?)null;
        var moved = had is null || Math.Abs(had.Value - wanted) >= DeadbandPercent;

        if (lost && had is not null)
        {
            Complain(channel.Identifier + ":lost", TimeSpan.FromMinutes(1),
                $"{channel.Describe()} left software control; something else is writing to this " +
                "header. Reclaiming it.");
        }

        if (!moved && !lost && !_released.Contains(channel.Identifier)) return;

        _hardware.SetPercent(channel.Identifier, wanted);
        _applied[channel.Identifier] = wanted;
        _released.Remove(channel.Identifier);
        _owned.Add(channel.Identifier);
        _failures = 0;

        _log.Info($"{channel.Describe()}: fan -> {wanted:0.#}% ({reason})");
    }

    /// <summary>
    /// Hands one header back on request and stops considering it ours. Unconditional, unlike the
    /// tick's release: an explicit "give this back" must reach the chip even if we believe it is
    /// already released, because that belief is the thing most likely to be wrong.
    /// </summary>
    public void Release(FanChannel channel)
    {
        _hardware.ReleaseToDefault(channel.Identifier);
        _released.Add(channel.Identifier);
        _applied.Remove(channel.Identifier);
        _owned.Remove(channel.Identifier);

        _log.Info($"{channel.Describe()}: handed back to the BIOS (released by request)");
    }

    void Release(FanChannel channel, string reason)
    {
        if (_released.Contains(channel.Identifier)) return;

        _hardware.ReleaseToDefault(channel.Identifier);
        _released.Add(channel.Identifier);
        _applied.Remove(channel.Identifier);
        _failures = 0;

        _log.Info($"{channel.Describe()}: handed back to the BIOS ({reason})");
    }

    // ---- adoption --------------------------------------------------------------------

    /// <summary>
    /// Commands a header to full and checks the paired tachometer actually responds.
    ///
    /// This is the guard against adopting the wrong channel. A mistyped identifier that happens to
    /// resolve would otherwise silently take over the pump or a CPU fan, and the first symptom
    /// would be thermal. Returns null when the header responded, or a description of what was
    /// wrong when it did not.
    ///
    /// Blocks for a couple of seconds while the fan spools, and is called with the service's tick
    /// monitor held, so GPU management pauses for that time. That is acceptable for a deliberate,
    /// one-off, user-initiated action and nowhere else.
    /// </summary>
    public string? Verify(FanChannel channel, Action<TimeSpan> wait)
    {
        if (channel.RpmSensorIdentifier is null)
            return "this header has no tachometer, so its response could not be confirmed";

        var before = TryRead(channel.Identifier)?.Rpm;

        var restore = _applied.TryGetValue(channel.Identifier, out var previous) ? previous : (double?)null;
        var wasReleased = _released.Contains(channel.Identifier);

        try
        {
            _hardware.SetPercent(channel.Identifier, channel.MaxSoftwarePercent);
            _owned.Add(channel.Identifier);
            _released.Remove(channel.Identifier);

            wait(TimeSpan.FromSeconds(3));
            _hardware.Refresh();

            var after = TryRead(channel.Identifier)?.Rpm;

            if (after is null or 0)
                return "this header reported no RPM at full speed - is anything plugged into it?";

            // A fan already at full will not rise, so a healthy high reading counts on its own.
            if (before is { } baseline && after <= baseline && after < 400)
                return $"this header did not speed up ({baseline} -> {after} RPM) - is it the right one?";

            return null;
        }
        finally
        {
            // Put the channel back the way it was, whatever the outcome. Leaving a header at 100%
            // because verification threw would be a nasty way to learn about an exception.
            if (wasReleased) { _hardware.ReleaseToDefault(channel.Identifier); _released.Add(channel.Identifier); }
            else if (restore is { } value) { _hardware.SetPercent(channel.Identifier, value); _applied[channel.Identifier] = value; }
        }
    }

    // ---- shutdown --------------------------------------------------------------------

    /// <summary>
    /// Hands every header we ever took over back to the BIOS. Called on service stop and on
    /// uninstall, so a machine is never left with a fan pinned by an app that is no longer there.
    /// </summary>
    public void ReleaseAll()
    {
        if (_disposed) return;

        foreach (var identifier in _owned.ToList())
        {
            try
            {
                _hardware.ReleaseToDefault(identifier);
                _released.Add(identifier);
                _applied.Remove(identifier);
                _log.Info($"released fan header {identifier} back to the BIOS");
            }
            catch (Exception ex)
            {
                _log.Error($"could not release fan header {identifier}: {ex.Message}");
            }
        }
    }

    /// <summary>Drops smoothing and applied state for a header, e.g. after its curve is edited.</summary>
    public void Reset(string controlIdentifier)
    {
        _engine.Reset(controlIdentifier);
        _applied.Remove(controlIdentifier);
    }

    public FanChannel? FindChannel(string? selector) => _hardware.Channels
        .FirstOrDefault(c => FanSelector.Matches(c.Identifier, c.Name, c.Index, selector));

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>
    /// Finds the channel a config refers to, falling back to chip and index if the identifier
    /// string has moved — a BIOS update or a library rename is enough to do that. The fallback is
    /// deliberately narrow and loud: anything less specific would risk driving the wrong fan.
    /// </summary>
    FanChannel? Resolve(FanConfig fan, Action<FanConfig> persist)
    {
        var exact = _hardware.Channels.FirstOrDefault(c =>
            string.Equals(c.Identifier, fan.ControlIdentifier, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        if (fan.ChipName is not { Length: > 0 } chip || fan.ControlIndex < 0) return null;

        var rebound = _hardware.Channels.FirstOrDefault(c =>
            string.Equals(c.ChipName, chip, StringComparison.OrdinalIgnoreCase) && c.Index == fan.ControlIndex);
        if (rebound is null) return null;

        _log.Warn($"fan header identifier changed from '{fan.ControlIdentifier}' to " +
                  $"'{rebound.Identifier}'; re-binding by chip and index ({chip} #{fan.ControlIndex})");

        persist(fan with { ControlIdentifier = rebound.Identifier, RpmSensorIdentifier = rebound.RpmSensorIdentifier });
        return rebound;
    }

    FanReading? TryRead(string identifier)
    {
        try
        {
            return _hardware.Read(identifier);
        }
        catch (Exception ex)
        {
            NoteFailure($"reading {identifier}", ex);
            return null;
        }
    }

    FanStatus StatusFor(FanConfig fan, FanChannel? channel, FanReading? reading,
                        GpuTelemetry? telemetry, FanDecision decision) => new()
    {
        ControlIdentifier = fan.ControlIdentifier,
        Name = fan.FriendlyName ?? channel?.Name,
        ChipName = channel?.ChipName ?? fan.ChipName,
        Index = channel?.Index ?? fan.ControlIndex,
        Managed = fan.Managed,
        Verified = fan.Verified,
        Present = channel is not null,
        SourceGpuUuid = fan.SourceGpuUuid,
        SourceTemperatureC = telemetry?.TemperatureC,
        CommandedPercent = decision.Percent,
        ReadbackPercent = reading?.Percent,
        Rpm = reading?.Rpm,
        Mode = reading?.Mode ?? FanControlMode.Undefined,
        Source = decision.Source,
        Reason = decision.Reason,
        Override = fan.Override,
        Curve = fan.Curve,
    };

    /// <summary>
    /// Counts a hardware failure and, past the budget, tries one reopen before disabling fan
    /// control entirely. Retrying a dead driver at 1 Hz forever would fill the log and achieve
    /// nothing; a restart of the service is the honest way back.
    /// </summary>
    void NoteFailure(string what, Exception ex)
    {
        _failures++;
        _log.Warn($"fan control: {what} failed ({ex.Message})");

        if (_failures < FailureBudget) return;

        _log.Error($"fan control has failed {_failures} times in a row; attempting to reopen the hardware");

        try
        {
            if (_hardware.TryReopen())
            {
                _failures = 0;
                _applied.Clear();
                _released.Clear();
                _log.Info("fan control reopened successfully");
                return;
            }
        }
        catch (Exception reopen)
        {
            _log.Error($"reopening fan control failed: {reopen.Message}");
        }

        _givenUp = true;
        _log.Error("fan control is disabled until the service restarts; the headers are on the BIOS curve");
    }

    void Complain(string key, TimeSpan interval, string message)
    {
        var now = _clock();
        if (_lastComplaint.TryGetValue(key, out var last) && now - last < interval) return;

        _lastComplaint[key] = now;
        _log.Warn(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardware.Dispose();
    }
}
