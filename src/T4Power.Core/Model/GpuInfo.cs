namespace T4Power.Core.Model;

/// <summary>
/// Static identity and capabilities of a GPU — everything that does not change while the
/// driver is loaded. Discovered once at enumeration.
/// </summary>
public sealed record GpuInfo
{
    /// <summary>NVML enumeration index. Informational only — <b>never</b> use it as a key.
    /// This machine has two GPUs and index order is not guaranteed stable across reboots
    /// or driver reloads. <see cref="Uuid"/> is the identity.</summary>
    public required int Index { get; init; }

    /// <summary>Stable identity, e.g. <c>GPU-fb0e16f4-561d-9314-9f1e-5b819256f1d8</c>.</summary>
    public required string Uuid { get; init; }

    public required string Name { get; init; }
    public string? Serial { get; init; }
    public string? PciBusId { get; init; }

    // --- power envelope, as reported by NVML. Never hardcode these. ---
    public required double MinPowerLimitW { get; init; }
    public required double MaxPowerLimitW { get; init; }
    public required double DefaultPowerLimitW { get; init; }

    /// <summary>False when the driver/GPU refuses power-limit management entirely.</summary>
    public required bool SupportsPowerLimit { get; init; }

    /// <summary>
    /// Supported graphics clocks in MHz, descending (T4: 1590 down to 300 in 15 MHz steps).
    /// Used to snap the clock slider to values the card actually offers. Empty if unavailable.
    /// </summary>
    public required IReadOnlyList<uint> SupportedGraphicsClocksMhz { get; init; }

    public uint MinGraphicsClockMhz => SupportedGraphicsClocksMhz.Count > 0 ? SupportedGraphicsClocksMhz[^1] : 0;
    public uint MaxGraphicsClockMhz => SupportedGraphicsClocksMhz.Count > 0 ? SupportedGraphicsClocksMhz[0] : 0;

    /// <summary>True when this GPU looks like a Tesla T4, used only to pick sensible defaults.</summary>
    public bool IsTeslaT4 => Name.Contains("T4", StringComparison.OrdinalIgnoreCase)
                             && Name.Contains("Tesla", StringComparison.OrdinalIgnoreCase);

    /// <summary>Snaps an arbitrary MHz value to the nearest clock the GPU actually supports.</summary>
    public uint SnapClock(uint mhz)
    {
        if (SupportedGraphicsClocksMhz.Count == 0) return mhz;

        var best = SupportedGraphicsClocksMhz[0];
        var bestDelta = long.MaxValue;
        foreach (var candidate in SupportedGraphicsClocksMhz)
        {
            var delta = Math.Abs((long)candidate - mhz);
            if (delta < bestDelta) { bestDelta = delta; best = candidate; }
        }
        return best;
    }

    public double ClampPowerW(double watts) => Math.Clamp(watts, MinPowerLimitW, MaxPowerLimitW);

    public string ShortId => Uuid.Length > 16 ? Uuid[..16] + "…" : Uuid;
}
