using T4Power.Core.Model;

namespace T4Power.Core.Nvml;

/// <summary>
/// An initialised NVML session. Owns <c>nvmlInit_v2</c>/<c>nvmlShutdown</c> and device
/// enumeration. Only the service creates one of these — the UI and CLI reach the GPU through
/// the service over IPC, which is what keeps them unelevated.
/// </summary>
public sealed class NvmlSession : IDisposable
{
    bool _disposed;

    NvmlSession(string? driverVersion, IReadOnlyList<GpuDevice> devices)
    {
        DriverVersion = driverVersion;
        Devices = devices;
    }

    public string? DriverVersion { get; }
    public IReadOnlyList<GpuDevice> Devices { get; }

    public static NvmlSession Open()
    {
        var rc = NvmlNative.nvmlInit_v2();
        if (rc != NvmlReturn.Success)
            throw new NvmlException(rc, nameof(NvmlNative.nvmlInit_v2));

        try
        {
            return new NvmlSession(ReadDriverVersion(), Enumerate());
        }
        catch
        {
            NvmlNative.nvmlShutdown();
            throw;
        }
    }

    public GpuDevice? FindByUuid(string uuid) =>
        Devices.FirstOrDefault(d => string.Equals(d.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NvmlNative.nvmlShutdown();
    }

    // ---- enumeration -----------------------------------------------------------------

    static string? ReadDriverVersion()
    {
        var buf = new byte[128];
        return NvmlNative.nvmlSystemGetDriverVersion(buf, (uint)buf.Length) == NvmlReturn.Success
            ? NvmlNative.Decode(buf)
            : null;
    }

    static IReadOnlyList<GpuDevice> Enumerate()
    {
        var rc = NvmlNative.nvmlDeviceGetCount_v2(out var count);
        if (rc != NvmlReturn.Success)
            throw new NvmlException(rc, nameof(NvmlNative.nvmlDeviceGetCount_v2));

        var devices = new List<GpuDevice>((int)count);
        for (var i = 0u; i < count; i++)
        {
            if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out var handle) != NvmlReturn.Success)
                continue; // a single unreadable GPU must not sink discovery of the rest

            var info = ReadInfo(handle, (int)i);
            if (info is not null) devices.Add(new GpuDevice(handle, info));
        }
        return devices;
    }

    static GpuInfo? ReadInfo(IntPtr handle, int index)
    {
        var uuidBuf = new byte[128];
        if (NvmlNative.nvmlDeviceGetUUID(handle, uuidBuf, (uint)uuidBuf.Length) != NvmlReturn.Success)
            return null; // without a UUID we have no stable key, so the device is unusable to us

        var nameBuf = new byte[128];
        var name = NvmlNative.nvmlDeviceGetName(handle, nameBuf, (uint)nameBuf.Length) == NvmlReturn.Success
            ? NvmlNative.Decode(nameBuf)
            : "Unknown GPU";

        var serialBuf = new byte[64];
        var serial = NvmlNative.nvmlDeviceGetSerial(handle, serialBuf, (uint)serialBuf.Length) == NvmlReturn.Success
            ? NvmlNative.Decode(serialBuf)
            : null;

        var pci = new NvmlPciInfo { BusIdLegacy = new byte[16], BusId = new byte[32] };
        var busId = NvmlNative.nvmlDeviceGetPciInfo_v3(handle, ref pci) == NvmlReturn.Success
            ? NvmlNative.Decode(pci.BusId)
            : null;

        // Power-limit support is discoverable read-only: if constraints come back, it works.
        var supportsPower = NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints(handle, out var minMw, out var maxMw)
            == NvmlReturn.Success;
        var defaultMw = NvmlNative.nvmlDeviceGetPowerManagementDefaultLimit(handle, out var dm) == NvmlReturn.Success
            ? dm
            : maxMw;

        return new GpuInfo
        {
            Index = index,
            Uuid = NvmlNative.Decode(uuidBuf),
            Name = name,
            Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
            PciBusId = string.IsNullOrWhiteSpace(busId) ? null : busId,
            SupportsPowerLimit = supportsPower,
            MinPowerLimitW = supportsPower ? minMw / 1000.0 : 0,
            MaxPowerLimitW = supportsPower ? maxMw / 1000.0 : 0,
            DefaultPowerLimitW = supportsPower ? defaultMw / 1000.0 : 0,
            SupportedGraphicsClocksMhz = ReadSupportedGraphicsClocks(handle),
        };
    }

    /// <summary>
    /// Graphics clocks available at the card's highest memory clock, descending.
    /// On the T4 this is 1590 → 300 MHz in 15 MHz steps (87 values).
    /// </summary>
    static IReadOnlyList<uint> ReadSupportedGraphicsClocks(IntPtr handle)
    {
        var memClock = HighestMemoryClock(handle);
        if (memClock is null) return [];

        uint count = 0;
        if (NvmlNative.nvmlDeviceGetSupportedGraphicsClocks(handle, memClock.Value, ref count, null)
            != NvmlReturn.InsufficientSize || count == 0)
            return [];

        var clocks = new uint[count];
        if (NvmlNative.nvmlDeviceGetSupportedGraphicsClocks(handle, memClock.Value, ref count, clocks)
            != NvmlReturn.Success)
            return [];

        var result = clocks.Take((int)count).ToArray();
        Array.Sort(result, static (a, b) => b.CompareTo(a)); // descending
        return result;
    }

    static uint? HighestMemoryClock(IntPtr handle)
    {
        uint count = 0;
        if (NvmlNative.nvmlDeviceGetSupportedMemoryClocks(handle, ref count, null)
            != NvmlReturn.InsufficientSize || count == 0)
        {
            // Fall back to the reported maximum when the enumeration is unavailable.
            return NvmlNative.nvmlDeviceGetMaxClockInfo(handle, NvmlClockType.Memory, out var maxMem)
                == NvmlReturn.Success ? maxMem : null;
        }

        var mems = new uint[count];
        if (NvmlNative.nvmlDeviceGetSupportedMemoryClocks(handle, ref count, mems) != NvmlReturn.Success)
            return null;

        return mems.Take((int)count).Max();
    }
}
