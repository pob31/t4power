using T4Power.Core.Model;

namespace T4Power.Core.Fans;

public enum FanDecisionSource
{
    /// <summary>The panic temperature was reached; everything else is outranked.</summary>
    Panic,

    /// <summary>A manual duty from the UI or CLI.</summary>
    Override,

    /// <summary>Normal operation: the curve, smoothed.</summary>
    Curve,

    /// <summary>No usable temperature, so the header is parked somewhere known-safe.</summary>
    FailSafe,

    /// <summary>The header is not managed; T4Power reports on it but writes nothing.</summary>
    Unmanaged,
}

/// <summary>What the engine wants the header doing, and why. The reason is surfaced verbatim in
/// the UI and in <c>--fans</c>, exactly as <see cref="Rules.RuleDecision.Reason"/> is.</summary>
public sealed record FanDecision
{
    public required FanDecisionSource Source { get; init; }
    public required string Reason { get; init; }

    /// <summary>Duty to command, or null when nothing should be written.</summary>
    public double? Percent { get; init; }

    /// <summary>True when the channel should be handed back to the BIOS rather than driven.</summary>
    public bool ReleaseToDefault { get; init; }

    public static FanDecision None(FanDecisionSource source, string reason) =>
        new() { Source = source, Reason = reason };
}

/// <summary>
/// Decides what duty a fan header should run at. Pure with respect to the hardware: it reads
/// telemetry and config and returns a decision, leaving every write to the caller. That is the
/// same split <see cref="Rules.RuleEngine"/> uses, and for the same reason — it is what makes the
/// smoothing testable without a motherboard.
/// </summary>
public sealed class FanCurveEngine
{
    /// <summary>
    /// The smoothing state for one header.
    ///
    /// <see cref="HeldTemperatureC"/> is the temperature the curve is actually being evaluated at,
    /// which lags the real reading — that lag *is* the smoothing. A reading only becomes a
    /// <see cref="CandidateTemperatureC"/> once it escapes the hysteresis band around the held
    /// value, and only replaces the held value once it has stayed out there for the response time.
    /// </summary>
    sealed class ChannelState
    {
        public double? HeldTemperatureC;
        public double? CandidateTemperatureC;
        public DateTimeOffset CandidateSince;
    }

    readonly Func<DateTimeOffset> _clock;

    readonly Dictionary<string, ChannelState> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Same rationale as <see cref="Rules.RuleEngine"/>'s gate: the engine carries mutable state
    /// across calls, and it is reached from both the service timer and the IPC handlers. A
    /// Dictionary written concurrently does not fail cleanly — it can spin forever, freezing the
    /// last decision in place, which for a fan means a duty that never updates again.
    /// </summary>
    readonly object _gate = new();

    public FanCurveEngine(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public FanDecision Evaluate(FanConfig config, GpuTelemetry? telemetry)
    {
        lock (_gate) return EvaluateCore(config, telemetry, _clock());
    }

    FanDecision EvaluateCore(FanConfig config, GpuTelemetry? telemetry, DateTimeOffset now)
    {
        if (!config.Managed)
            return FanDecision.None(FanDecisionSource.Unmanaged, "not managed by T4Power");

        // No temperature, or one too old to act on. This is the in-process replacement for the old
        // PowerShell relay's "write 90 on failure", and it is strictly better: park the header in
        // a state chosen for safety instead of inventing a reading and pretending to steer.
        if (StaleReason(config, telemetry, now) is { } stale)
        {
            _state.Remove(config.ControlIdentifier);
            return FailSafe(config, stale);
        }

        var temperatureC = (double)telemetry!.TemperatureC!.Value;

        // Above the panic point nothing else gets a say - not the smoothing, and not a manual
        // override. Someone who pinned the fan to 30% must not be able to cook the card.
        if (temperatureC >= config.PanicTemperatureC)
        {
            var state = StateFor(config.ControlIdentifier);
            state.HeldTemperatureC = temperatureC;
            state.CandidateTemperatureC = null;

            return new FanDecision
            {
                Source = FanDecisionSource.Panic,
                Reason = $"panic: {temperatureC:0.#} C >= {config.PanicTemperatureC:0.#} C, forcing 100%",
                Percent = 100,
            };
        }

        if (config.Override is { } ovr && !ovr.IsExpired(now))
        {
            var until = ovr.ExpiresUtc is { } expires
                ? $", expires in {FormatRemaining(expires - now)}"
                : "";

            return new FanDecision
            {
                Source = FanDecisionSource.Override,
                Reason = $"manual override -> {ovr.Percent:0.#}%{until}",
                Percent = Math.Clamp(ovr.Percent, 0, 100),
            };
        }

        return EvaluateCurve(config, temperatureC, now);
    }

    FanDecision EvaluateCurve(FanConfig config, double temperatureC, DateTimeOffset now)
    {
        var curve = config.Curve;
        var hysteresis = curve.Hysteresis;
        var state = StateFor(config.ControlIdentifier);

        // First reading for this header: adopt it as-is. Starting the smoothing from a made-up
        // value would mean the first minute of every service start runs on fiction.
        if (state.HeldTemperatureC is null)
        {
            state.HeldTemperatureC = temperatureC;
            state.CandidateTemperatureC = null;
        }
        else
        {
            var held = state.HeldTemperatureC.Value;
            var atLimit = hysteresis.IgnoreAtLimits
                          && (temperatureC <= curve.MinTemperatureC || temperatureC >= curve.MaxTemperatureC);

            var rising = temperatureC >= held + hysteresis.HysteresisUpC;
            var falling = temperatureC <= held - hysteresis.HysteresisDownC;

            if (atLimit)
            {
                state.HeldTemperatureC = temperatureC;
                state.CandidateTemperatureC = null;
            }
            else if (rising || falling)
            {
                // A candidate that flips direction is a new candidate, so its timer restarts.
                // Without this, a temperature oscillating across the band would accumulate dwell
                // it never actually held and step the fan on noise.
                var reversed = state.CandidateTemperatureC is { } candidate
                               && Math.Sign(candidate - held) != Math.Sign(temperatureC - held);

                if (state.CandidateTemperatureC is null || reversed)
                    state.CandidateSince = now;

                state.CandidateTemperatureC = temperatureC;

                var response = rising
                    ? hysteresis.ResponseTimeUpSeconds
                    : hysteresis.ResponseTimeDownSeconds;

                if ((now - state.CandidateSince).TotalSeconds >= response)
                {
                    state.HeldTemperatureC = temperatureC;
                    state.CandidateTemperatureC = null;
                }
            }
            else
            {
                // Back inside the band before the timer expired: the change was noise after all.
                state.CandidateTemperatureC = null;
            }
        }

        var effective = Math.Clamp(state.HeldTemperatureC!.Value, curve.MinTemperatureC, curve.MaxTemperatureC);
        var percent = curve.Evaluate(effective);

        var reason = $"curve: {temperatureC:0.#} C -> {percent:0.#}%";

        if (state.CandidateTemperatureC is { } pending)
        {
            var response = pending > state.HeldTemperatureC
                ? hysteresis.ResponseTimeUpSeconds
                : hysteresis.ResponseTimeDownSeconds;
            var remaining = TimeSpan.FromSeconds(response) - (now - state.CandidateSince);
            var direction = pending > state.HeldTemperatureC ? "up" : "down";

            reason += $" (holding at {state.HeldTemperatureC:0.#} C, {FormatRemaining(remaining)} to step {direction})";
        }

        return new FanDecision
        {
            Source = FanDecisionSource.Curve,
            Reason = reason,
            Percent = percent,
        };
    }

    /// <summary>Why this header cannot be steered right now, or null when it can.</summary>
    static string? StaleReason(FanConfig config, GpuTelemetry? telemetry, DateTimeOffset now)
    {
        if (telemetry is null) return "no telemetry for the source GPU";
        if (telemetry.TemperatureC is null) return "the source GPU is not reporting a temperature";

        var age = now - telemetry.TimestampUtc;
        return age > TimeSpan.FromSeconds(config.SensorTimeoutSeconds)
            ? $"the source GPU's temperature is {age.TotalSeconds:0}s old"
            : null;
    }

    static FanDecision FailSafe(FanConfig config, string why) => config.FailSafe switch
    {
        FanFailSafe.FullSpeed => new FanDecision
        {
            Source = FanDecisionSource.FailSafe,
            Reason = $"{why}; holding the header at full speed",
            Percent = 100,
        },

        FanFailSafe.FixedPercent => new FanDecision
        {
            Source = FanDecisionSource.FailSafe,
            Reason = $"{why}; holding the header at {config.FailSafePercent:0.#}%",
            Percent = Math.Clamp(config.FailSafePercent, 0, 100),
        },

        _ => new FanDecision
        {
            Source = FanDecisionSource.FailSafe,
            Reason = $"{why}; handing the header back to the BIOS",
            ReleaseToDefault = true,
        },
    };

    ChannelState StateFor(string identifier) =>
        _state.TryGetValue(identifier, out var state) ? state : _state[identifier] = new ChannelState();

    static string FormatRemaining(TimeSpan span) => span switch
    {
        { TotalSeconds: < 1 } => "moments",
        { TotalMinutes: < 1 } => $"{span.TotalSeconds:0}s",
        { TotalHours: < 1 } => $"{span.TotalMinutes:0}m",
        _ => $"{span.TotalHours:0.#}h",
    };

    /// <summary>Drops smoothing state for a header, e.g. after its curve is edited.</summary>
    public void Reset(string identifier)
    {
        lock (_gate) _state.Remove(identifier);
    }
}
