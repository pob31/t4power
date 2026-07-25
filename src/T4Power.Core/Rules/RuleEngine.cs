using T4Power.Core.Model;

namespace T4Power.Core.Rules;

public enum DecisionSource
{
    /// <summary>A manual override from the UI or CLI.</summary>
    Override,

    /// <summary>The temperature backstop fired, outranking everything else.</summary>
    ThermalGuard,

    /// <summary>A matching auto-switch rule.</summary>
    Rule,

    /// <summary>Nothing matched, so the configured default profile applies.</summary>
    Default,

    /// <summary>The GPU is not managed; T4Power reports on it but writes nothing.</summary>
    Unmanaged,
}

/// <summary>What the engine wants applied, and why. The reason is surfaced verbatim in the UI
/// and in <c>--status</c> so the current state is never mysterious.</summary>
public sealed record RuleDecision
{
    public required DecisionSource Source { get; init; }
    public required string Reason { get; init; }

    /// <summary>Null when nothing should be applied (unmanaged GPU, or a missing profile).</summary>
    public DesiredState? Desired { get; init; }

    public static RuleDecision None(DecisionSource source, string reason) =>
        new() { Source = source, Reason = reason };
}

/// <summary>The concrete GPU state a decision resolves to.</summary>
public sealed record DesiredState
{
    public string? ProfileName { get; init; }
    public double? PowerLimitW { get; init; }
    public ClockLock? LockClocks { get; init; }

    /// <summary>True when clocks should be explicitly released rather than left as they are.</summary>
    public bool UnlockClocks { get; init; }

    public static DesiredState FromProfile(Profile p) => new()
    {
        ProfileName = p.Name,
        PowerLimitW = p.PowerLimitW,
        LockClocks = p.LockClocks,
        UnlockClocks = p.LockClocks is null,
    };

    public string Describe()
    {
        var power = PowerLimitW is null ? "power unchanged" : $"{PowerLimitW:0.#} W";
        var clocks = LockClocks is not null ? LockClocks.ToString()
            : UnlockClocks ? "clocks unlocked"
            : "clocks unchanged";
        return ProfileName is null ? $"{power}, {clocks}" : $"{ProfileName} ({power}, {clocks})";
    }
}

/// <summary>
/// Decides which profile a GPU should be in. Pure with respect to the GPU: it reads telemetry
/// and config and returns a decision, leaving all NVML writes to the caller. That separation is
/// what makes the hysteresis testable without hardware.
/// </summary>
public sealed class RuleEngine
{
    readonly Func<DateTimeOffset> _clock;

    /// <summary>Per GPU, per rule index: the last time that rule's condition was true. This is
    /// the whole of the hysteresis state.</summary>
    readonly Dictionary<string, Dictionary<int, DateTimeOffset>> _lastTrue =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The engine carries mutable hysteresis state across calls, so it is not free-threaded.
    /// Callers are expected to serialise, but guarding here too means a caller that gets it
    /// wrong sees contention rather than a corrupted Dictionary — which manifests as the engine
    /// spinning forever and freezing the last decision in place, not as a clean exception.
    /// </summary>
    readonly object _gate = new();

    public RuleEngine(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public RuleDecision Evaluate(GpuConfig config, GpuTelemetry telemetry, IReadOnlySet<string> runningProcesses)
    {
        lock (_gate) return EvaluateCore(config, telemetry, runningProcesses);
    }

    RuleDecision EvaluateCore(GpuConfig config, GpuTelemetry telemetry, IReadOnlySet<string> runningProcesses)
    {
        var now = _clock();

        if (!config.Managed)
            return RuleDecision.None(DecisionSource.Unmanaged, "not managed by T4Power");

        // Safety first: the T4 is passively cooled and relies entirely on chassis airflow, so
        // the thermal backstop outranks even an explicit manual override.
        if (config.ThermalGuardC is { } guard && telemetry.TemperatureC is { } temp && temp >= guard)
        {
            var eco = config.FindProfile(Profile.Eco) ?? config.Profiles.LastOrDefault();
            if (eco is not null)
            {
                return new RuleDecision
                {
                    Source = DecisionSource.ThermalGuard,
                    Reason = $"thermal guard: {temp} C >= {guard} C, forcing {eco.Name}",
                    Desired = DesiredState.FromProfile(eco),
                };
            }
        }

        if (config.Override is { } ovr && !ovr.IsExpired(now))
        {
            var until = ovr.ExpiresUtc is { } exp
                ? $", expires in {FormatRemaining(exp - now)}"
                : "";

            if (ovr.ProfileName is not null)
            {
                var profile = config.FindProfile(ovr.ProfileName);
                if (profile is not null)
                {
                    return new RuleDecision
                    {
                        Source = DecisionSource.Override,
                        Reason = $"manual override -> {profile.Name}{until}",
                        Desired = DesiredState.FromProfile(profile),
                    };
                }
            }

            return new RuleDecision
            {
                Source = DecisionSource.Override,
                Reason = $"manual override{until}",
                Desired = new DesiredState
                {
                    ProfileName = ovr.ProfileName,
                    PowerLimitW = ovr.PowerLimitW,
                    LockClocks = ovr.LockClocks,
                    UnlockClocks = ovr.UnlockClocks,
                },
            };
        }

        return EvaluateRules(config, telemetry, runningProcesses, now);
    }

    RuleDecision EvaluateRules(GpuConfig config, GpuTelemetry telemetry,
                               IReadOnlySet<string> runningProcesses, DateTimeOffset now)
    {
        var state = _lastTrue.TryGetValue(config.Uuid, out var s)
            ? s
            : _lastTrue[config.Uuid] = [];

        // Evaluate every rule's condition first so the sticky timestamps stay current even for
        // rules that lose to a higher-priority one this tick.
        var ordered = config.Rules
            .Select((rule, index) => (rule, index))
            .Where(x => x.rule.Enabled)
            .OrderByDescending(x => x.rule.Priority)
            .ToList();

        foreach (var (rule, index) in ordered)
        {
            if (ConditionHolds(rule, telemetry, runningProcesses))
                state[index] = now;
        }

        foreach (var (rule, index) in ordered)
        {
            var conditionNow = ConditionHolds(rule, telemetry, runningProcesses);
            var lingering = false;

            if (!conditionNow && rule.DwellSeconds > 0 && state.TryGetValue(index, out var last))
                lingering = now - last < TimeSpan.FromSeconds(rule.DwellSeconds);

            if (!conditionNow && !lingering) continue;

            var profile = config.FindProfile(rule.ProfileName);
            if (profile is null) continue; // rule names a profile that no longer exists

            var reason = lingering
                ? $"{rule.Describe()} - holding for {FormatRemaining(TimeSpan.FromSeconds(rule.DwellSeconds) - (now - state[index]))} more"
                : rule.Describe();

            return new RuleDecision
            {
                Source = DecisionSource.Rule,
                Reason = reason,
                Desired = DesiredState.FromProfile(profile),
            };
        }

        var fallback = config.FindProfile(config.DefaultProfile);
        return fallback is null
            ? RuleDecision.None(DecisionSource.Default, $"no rule matched and no profile '{config.DefaultProfile}'")
            : new RuleDecision
            {
                Source = DecisionSource.Default,
                Reason = $"no rule matched -> default {fallback.Name}",
                Desired = DesiredState.FromProfile(fallback),
            };
    }

    static bool ConditionHolds(Rule rule, GpuTelemetry telemetry, IReadOnlySet<string> runningProcesses) =>
        rule.Type switch
        {
            RuleType.ProcessName => rule.Match.Any(m =>
                runningProcesses.Contains(ProcessWatcher.Normalise(m))),

            // Any context on the card counts, even at 0% utilisation: a process holding a CUDA
            // context between kernels is still a workload, and dropping clocks under it is
            // exactly the flapping this design is trying to avoid.
            RuleType.GpuActivity => telemetry.HasActiveContext
                                    || telemetry.GpuUtilPercent >= rule.UtilThreshold,

            RuleType.Idle => true,
            _ => false,
        };

    static string FormatRemaining(TimeSpan span) => span switch
    {
        { TotalSeconds: < 1 } => "moments",
        { TotalMinutes: < 1 } => $"{span.TotalSeconds:0}s",
        { TotalHours: < 1 } => $"{span.TotalMinutes:0}m",
        _ => $"{span.TotalHours:0.#}h",
    };

    /// <summary>Drops hysteresis state for a GPU, e.g. after its config is edited.</summary>
    public void Reset(string uuid)
    {
        lock (_gate) _lastTrue.Remove(uuid);
    }
}
