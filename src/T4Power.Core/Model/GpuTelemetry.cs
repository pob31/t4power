using T4Power.Core.Nvml;

namespace T4Power.Core.Model;

/// <summary>A point-in-time reading of a GPU. Cheap enough to poll at 1 Hz.</summary>
public sealed record GpuTelemetry
{
    public required string Uuid { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }

    public double? PowerDrawW { get; init; }
    public double? PowerLimitW { get; init; }

    /// <summary>The limit actually in force, which can be below the requested one.</summary>
    public double? EnforcedPowerLimitW { get; init; }

    public uint? SmClockMhz { get; init; }
    public uint? MemClockMhz { get; init; }
    public uint? TemperatureC { get; init; }
    public uint? GpuUtilPercent { get; init; }
    public uint? MemUtilPercent { get; init; }
    public NvmlPState? PState { get; init; }
    public NvmlClocksEventReason EventReasons { get; init; }

    /// <summary>PIDs holding a compute or graphics context on this GPU. The primary
    /// "is anything actually using the card" signal for the rule engine.</summary>
    public IReadOnlyList<uint> ActivePids { get; init; } = [];

    public bool HasActiveContext => ActivePids.Count > 0;

    /// <summary>Reasons worth surfacing in the UI — the benign idle/no-reason bits are dropped.</summary>
    public IEnumerable<string> InterestingReasons()
    {
        if (EventReasons.HasFlag(NvmlClocksEventReason.SwPowerCap)) yield return "Power cap";
        if (EventReasons.HasFlag(NvmlClocksEventReason.HwSlowdown)) yield return "HW slowdown";
        if (EventReasons.HasFlag(NvmlClocksEventReason.SwThermalSlowdown)) yield return "Thermal (SW)";
        if (EventReasons.HasFlag(NvmlClocksEventReason.HwThermalSlowdown)) yield return "Thermal (HW)";
        if (EventReasons.HasFlag(NvmlClocksEventReason.HwPowerBrakeSlowdown)) yield return "Power brake";
        if (EventReasons.HasFlag(NvmlClocksEventReason.ApplicationsClocksSetting)) yield return "App clocks";
        if (EventReasons.HasFlag(NvmlClocksEventReason.SyncBoost)) yield return "Sync boost";
    }
}
