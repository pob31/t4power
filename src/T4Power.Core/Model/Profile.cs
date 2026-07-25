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

    // --- Defaults, from measurements on a Tesla T4 (driver 610.74, MCDM) ---
    //
    // Unlocked        -> 300 MHz, P8,  ~10 W   (an idle card parks itself at the bottom)
    // Pinned 300-900  -> 300 MHz, P8,  ~10 W,  53 C
    // Pinned 1590     -> 1590 MHz, P0,  34 W,  53 C
    //
    // The important lesson is that "unlocked" cannot raise anything. It means "let the card
    // decide", and an idle card decides P8. Forcing P0 requires pinning the clock FLOOR high,
    // which is what Max does.

    public const string Eco = "Eco";
    public const string Balanced = "Balanced";
    public const string Max = "Max";

    /// <summary>
    /// Default profiles for a T4, forming a forced-low / automatic / forced-high scale:
    /// <list type="bullet">
    ///   <item><b>Eco</b> caps clocks low, for minimum idle draw and heat.</item>
    ///   <item><b>Balanced</b> unlocks and lets the card manage itself.</item>
    ///   <item><b>Max</b> pins clocks at the top, holding the card in P0 so a workload gets
    ///   full performance immediately rather than waiting for the card to ramp.</item>
    /// </list>
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
            LockClocks = null,
        },
        new Profile
        {
            Name = Max,
            PowerLimitW = info.MaxPowerLimitW,
            // Floor and ceiling both at the top: this is what pins the card in P0.
            LockClocks = new ClockLock
            {
                MinMhz = info.MaxGraphicsClockMhz,
                MaxMhz = info.MaxGraphicsClockMhz,
            },
        },
    ];

    /// <summary>
    /// Defaults for any other GPU. Eco and Balanced leave clocks alone — a consumer card idles
    /// down perfectly well by itself, and pinning a display adapter low would hurt desktop
    /// responsiveness for no benefit.
    ///
    /// Max still pins the clock at the top, for the same reason as on the T4: a card that idles
    /// to P8 between bursts of work has to ramp back up each time, and for a latency-sensitive
    /// workload that ramp is exactly what shows up as a glitch.
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
        new Profile
        {
            Name = Max,
            PowerLimitW = info.MaxPowerLimitW,
            LockClocks = info.MaxGraphicsClockMhz > 0
                ? new ClockLock { MinMhz = info.MaxGraphicsClockMhz, MaxMhz = info.MaxGraphicsClockMhz }
                : null,
        },
    ];

    public static IReadOnlyList<Profile> DefaultsFor(GpuInfo info) =>
        info.IsTeslaT4 ? DefaultsForT4(info) : DefaultsForGeneric(info);
}
