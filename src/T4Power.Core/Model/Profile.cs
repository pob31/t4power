namespace T4Power.Core.Model;

/// <summary>An SM clock lock. Null on a profile means "release the lock".</summary>
public sealed record ClockLock
{
    public required uint MinMhz { get; init; }
    public required uint MaxMhz { get; init; }
    public override string ToString() => $"{MinMhz}-{MaxMhz} MHz";
}

/// <summary>
/// A named power/clock state. Both knobs move together because on a T4 neither alone is
/// sufficient: the power limit governs sustained load (a 60-70 W band), while the clock lock
/// is the only thing that changes idle draw and temperature.
/// </summary>
public sealed record Profile
{
    public required string Name { get; init; }

    /// <summary>Power limit in watts. Null leaves the current limit alone.</summary>
    public double? PowerLimitW { get; init; }

    /// <summary>Clock lock to apply. Null means unlock (let the GPU manage its own clocks).</summary>
    public ClockLock? LockClocks { get; init; }

    public string Describe()
    {
        var power = PowerLimitW is null ? "power unchanged" : $"{PowerLimitW:0.#} W";
        var clocks = LockClocks is null ? "clocks unlocked" : LockClocks.ToString();
        return $"{power}, {clocks}";
    }

    // --- Defaults, from the measured spike on this machine ---
    // Baseline (unlocked, P0): 36 W / 63 C at idle.
    // Locked to 300-900 MHz:   ~10 W / 52 C at idle.

    public const string Eco = "Eco";
    public const string Balanced = "Balanced";
    public const string Max = "Max";

    /// <summary>
    /// Default profiles for a T4. Eco exists to fix the card idling at P0/1590 MHz; Max releases
    /// the lock entirely so a workload gets the full 1590 MHz and 70 W.
    /// </summary>
    public static IReadOnlyList<Profile> DefaultsForT4(GpuInfo info) =>
    [
        new Profile
        {
            Name = Eco,
            PowerLimitW = info.MinPowerLimitW,
            LockClocks = new ClockLock { MinMhz = info.MinGraphicsClockMhz, MaxMhz = info.SnapClock(900) },
        },
        new Profile
        {
            Name = Balanced,
            PowerLimitW = info.MaxPowerLimitW,
            LockClocks = new ClockLock { MinMhz = info.MinGraphicsClockMhz, MaxMhz = info.SnapClock(1290) },
        },
        new Profile
        {
            Name = Max,
            PowerLimitW = info.MaxPowerLimitW,
            LockClocks = null,
        },
    ];

    /// <summary>
    /// Defaults for any other GPU. A consumer card already idles properly on its own, so the
    /// Eco profile only trims the power ceiling rather than pinning clocks — locking clocks on
    /// a display GPU would hurt desktop responsiveness for no benefit.
    /// </summary>
    public static IReadOnlyList<Profile> DefaultsForGeneric(GpuInfo info) =>
    [
        new Profile { Name = Eco, PowerLimitW = info.MinPowerLimitW, LockClocks = null },
        new Profile
        {
            Name = Balanced,
            PowerLimitW = Math.Round((info.MinPowerLimitW + info.MaxPowerLimitW) / 2),
            LockClocks = null,
        },
        new Profile { Name = Max, PowerLimitW = info.MaxPowerLimitW, LockClocks = null },
    ];

    public static IReadOnlyList<Profile> DefaultsFor(GpuInfo info) =>
        info.IsTeslaT4 ? DefaultsForT4(info) : DefaultsForGeneric(info);
}
