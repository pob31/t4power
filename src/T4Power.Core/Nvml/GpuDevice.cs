using T4Power.Core.Model;

namespace T4Power.Core.Nvml;

/// <summary>
/// A single NVML device: identity, capabilities, telemetry, and the two write operations that
/// matter (power limit, locked clocks). Every write is clamped to NVML-reported constraints.
/// </summary>
public sealed class GpuDevice
{
    readonly IntPtr _handle;

    internal GpuDevice(IntPtr handle, GpuInfo info)
    {
        _handle = handle;
        Info = info;
    }

    public GpuInfo Info { get; }
    public string Uuid => Info.Uuid;

    /// <summary>
    /// Null until a locked-clocks write has been attempted; false once one has come back
    /// NotSupported. There is no read-only probe for this capability — the only way to learn
    /// it is to try.
    /// </summary>
    public bool? LockedClocksSupported { get; private set; }

    // ---- reads -----------------------------------------------------------------------

    public GpuTelemetry ReadTelemetry()
    {
        var pids = new List<uint>();
        pids.AddRange(TryGetProcessPids(compute: true));
        foreach (var pid in TryGetProcessPids(compute: false))
            if (!pids.Contains(pid)) pids.Add(pid);

        return new GpuTelemetry
        {
            Uuid = Info.Uuid,
            TimestampUtc = DateTimeOffset.UtcNow,
            PowerDrawW = TryReadMilli(NvmlNative.nvmlDeviceGetPowerUsage),
            PowerLimitW = TryReadMilli(NvmlNative.nvmlDeviceGetPowerManagementLimit),
            EnforcedPowerLimitW = TryReadMilli(NvmlNative.nvmlDeviceGetEnforcedPowerLimit),
            SmClockMhz = TryReadClock(NvmlClockType.Sm),
            MemClockMhz = TryReadClock(NvmlClockType.Memory),
            TemperatureC = NvmlNative.nvmlDeviceGetTemperature(_handle, NvmlTemperatureSensor.Gpu, out var t)
                == NvmlReturn.Success ? t : null,
            GpuUtilPercent = TryReadUtil()?.Gpu,
            MemUtilPercent = TryReadUtil()?.Memory,
            PState = NvmlNative.nvmlDeviceGetPerformanceState(_handle, out var p) == NvmlReturn.Success ? p : null,
            EventReasons = NvmlNative.nvmlDeviceGetCurrentClocksEventReasons(_handle, out var r)
                == NvmlReturn.Success ? (NvmlClocksEventReason)r : NvmlClocksEventReason.None,
            ActivePids = pids,
        };
    }

    public double? GetPowerLimitW() => TryReadMilli(NvmlNative.nvmlDeviceGetPowerManagementLimit);

    // ---- writes (require elevation; the service performs these as LocalSystem) --------

    /// <summary>
    /// Sets the power management limit, clamped to the NVML-reported constraints.
    /// Returns the value actually requested in watts.
    /// </summary>
    public double SetPowerLimitW(double watts)
    {
        if (!Info.SupportsPowerLimit)
            throw new NvmlException(NvmlReturn.NotSupported, nameof(NvmlNative.nvmlDeviceSetPowerManagementLimit),
                $"{Info.Name} does not support power limit management");

        var clamped = Info.ClampPowerW(watts);
        var mw = (uint)Math.Round(clamped * 1000.0);

        var rc = NvmlNative.nvmlDeviceSetPowerManagementLimit(_handle, mw);
        if (rc != NvmlReturn.Success)
            throw new NvmlException(rc, nameof(NvmlNative.nvmlDeviceSetPowerManagementLimit), $"{clamped:0.#} W");

        return clamped;
    }

    public double ResetPowerLimitToDefault() => SetPowerLimitW(Info.DefaultPowerLimitW);

    /// <summary>
    /// Locks the SM clock to the given range, snapped to clocks the card supports. This is the
    /// knob that actually moves idle power on a T4: measured 36 W/63 °C at an unlocked 1590 MHz
    /// versus ~10 W/52 °C locked to 300–900 MHz.
    /// </summary>
    public (uint Min, uint Max) SetLockedClocks(uint minMhz, uint maxMhz)
    {
        var min = Info.SnapClock(minMhz);
        var max = Info.SnapClock(maxMhz);
        if (min > max) (min, max) = (max, min);

        var rc = NvmlNative.nvmlDeviceSetGpuLockedClocks(_handle, min, max);
        if (rc != NvmlReturn.Success)
        {
            if (rc is NvmlReturn.NotSupported or NvmlReturn.DeprecatedFunction)
                LockedClocksSupported = false;
            throw new NvmlException(rc, nameof(NvmlNative.nvmlDeviceSetGpuLockedClocks), $"{min}-{max} MHz");
        }

        LockedClocksSupported = true;
        return (min, max);
    }

    public void ResetLockedClocks()
    {
        var rc = NvmlNative.nvmlDeviceResetGpuLockedClocks(_handle);
        if (rc != NvmlReturn.Success)
        {
            if (rc is NvmlReturn.NotSupported or NvmlReturn.DeprecatedFunction)
            {
                LockedClocksSupported = false;
                return; // nothing was locked, so nothing to undo
            }
            throw new NvmlException(rc, nameof(NvmlNative.nvmlDeviceResetGpuLockedClocks));
        }
        LockedClocksSupported = true;
    }

    /// <summary>
    /// Restores the card to a clean state: default power limit, no clock lock. Used on service
    /// shutdown and uninstall so the machine is never left clamped.
    /// </summary>
    public void RestoreDefaults()
    {
        try { ResetLockedClocks(); } catch (NvmlException) { /* best effort */ }
        try { ResetPowerLimitToDefault(); } catch (NvmlException) { /* best effort */ }
    }

    // ---- internals -------------------------------------------------------------------

    delegate NvmlReturn MilliReader(IntPtr device, out uint value);

    double? TryReadMilli(MilliReader read) =>
        read(_handle, out var mw) == NvmlReturn.Success ? mw / 1000.0 : null;

    uint? TryReadClock(NvmlClockType type) =>
        NvmlNative.nvmlDeviceGetClockInfo(_handle, type, out var mhz) == NvmlReturn.Success ? mhz : null;

    NvmlUtilization? TryReadUtil() =>
        NvmlNative.nvmlDeviceGetUtilizationRates(_handle, out var u) == NvmlReturn.Success ? u : null;

    IReadOnlyList<uint> TryGetProcessPids(bool compute)
    {
        uint count = 0;
        var probe = compute
            ? NvmlNative.nvmlDeviceGetComputeRunningProcesses_v3(_handle, ref count, null)
            : NvmlNative.nvmlDeviceGetGraphicsRunningProcesses_v3(_handle, ref count, null);

        // Success with count 0 means genuinely nothing is running.
        if (probe == NvmlReturn.Success || count == 0) return [];
        if (probe != NvmlReturn.InsufficientSize) return [];

        var infos = new NvmlProcessInfo[count];
        var rc = compute
            ? NvmlNative.nvmlDeviceGetComputeRunningProcesses_v3(_handle, ref count, infos)
            : NvmlNative.nvmlDeviceGetGraphicsRunningProcesses_v3(_handle, ref count, infos);
        if (rc != NvmlReturn.Success) return [];

        var pids = new uint[count];
        for (var i = 0; i < count; i++) pids[i] = infos[i].Pid;
        return pids;
    }
}
