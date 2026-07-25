using System.Text.Json.Serialization;

namespace T4Power.Core.Model;

public enum RuleType
{
    /// <summary>Matches when a named executable is running anywhere on the system. Fires before
    /// the app touches the GPU, so the card is already ramped when work starts.</summary>
    ProcessName,

    /// <summary>Matches when something holds a compute/graphics context on this GPU, or
    /// utilisation exceeds a threshold. App-agnostic: catches workloads nobody listed.</summary>
    GpuActivity,

    /// <summary>The fallback, matching whenever nothing above it does.</summary>
    Idle,
}

/// <summary>
/// One auto-switch rule. Highest <see cref="Priority"/> that matches wins; <see cref="RuleType.Idle"/>
/// should sit at the bottom as the catch-all.
/// </summary>
public sealed record Rule
{
    public required RuleType Type { get; init; }

    /// <summary>Name of the profile to apply. Serialised as "profile" to keep config.json terse.</summary>
    [JsonPropertyName("profile")]
    public required string ProfileName { get; init; }

    /// <summary>Higher wins. Ties are broken by order in the list.</summary>
    public int Priority { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Executable names for <see cref="RuleType.ProcessName"/>, matched
    /// case-insensitively with or without the <c>.exe</c> suffix.</summary>
    public IReadOnlyList<string> Match { get; init; } = [];

    /// <summary>GPU utilisation percent above which <see cref="RuleType.GpuActivity"/> fires even
    /// with no visible process context.</summary>
    public uint UtilThreshold { get; init; } = 5;

    /// <summary>
    /// How long this rule keeps applying after its condition stops being true.
    ///
    /// This is the hysteresis, and it has exactly one meaning everywhere: ramp-up is immediate
    /// (a true condition wins at once), ramp-down lingers. Without it the profile flaps between
    /// Eco and Max on brief gaps between kernels. Unused by <see cref="RuleType.Idle"/>, whose
    /// condition is always true.
    /// </summary>
    public int DwellSeconds { get; init; } = 60;

    public string Describe() => Type switch
    {
        RuleType.ProcessName => $"while any of [{string.Join(", ", Match)}] is running -> {ProfileName}"
                                + $" (holds {DwellSeconds}s after it exits)",
        RuleType.GpuActivity => $"while the GPU is in use (or >{UtilThreshold}% util) -> {ProfileName}"
                                + $" (holds {DwellSeconds}s after it goes quiet)",
        RuleType.Idle => $"otherwise -> {ProfileName}",
        _ => ProfileName,
    };

    /// <summary>
    /// Sensible starting rules: named apps win, any real GPU work ramps up, everything else
    /// falls back to Eco after a dwell period.
    /// </summary>
    public static IReadOnlyList<Rule> Defaults() =>
    [
        new Rule
        {
            Type = RuleType.ProcessName,
            Priority = 100,
            ProfileName = Profile.Max,
            // Deliberately empty and disabled: guessing at the user's workloads would cause
            // surprising ramp-ups. The GpuActivity rule below already covers anything unlisted.
            Match = [],
            Enabled = false,
            DwellSeconds = 60,
        },
        new Rule
        {
            Type = RuleType.GpuActivity,
            Priority = 50,
            ProfileName = Profile.Max,
            UtilThreshold = 5,
            // Hold Max for five minutes after the GPU goes quiet. Long enough to ride out the
            // gaps between successive jobs without dropping clocks in between.
            DwellSeconds = 300,
        },
        new Rule
        {
            // The catch-all. Its condition is always true, so DwellSeconds does not apply.
            Type = RuleType.Idle,
            Priority = 0,
            ProfileName = Profile.Eco,
            DwellSeconds = 0,
        },
    ];
}
