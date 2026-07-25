using System.Runtime.InteropServices;
using System.Text;

namespace T4Power.Core.Nvml;

public enum NvmlClockType { Graphics = 0, Sm = 1, Memory = 2, Video = 3 }

public enum NvmlTemperatureSensor { Gpu = 0 }

/// <summary>
/// Performance state. P0 is maximum, P15 minimum, 32 means unknown. Every level is named so
/// that <c>ToString()</c> yields "P8" rather than a bare "8" in status output and JSON.
/// </summary>
public enum NvmlPState
{
    P0 = 0, P1 = 1, P2 = 2, P3 = 3, P4 = 4, P5 = 5, P6 = 6, P7 = 7,
    P8 = 8, P9 = 9, P10 = 10, P11 = 11, P12 = 12, P13 = 13, P14 = 14, P15 = 15,
    Unknown = 32,
}

/// <summary>
/// Bitmask returned by <c>nvmlDeviceGetCurrentClocksEventReasons</c>. Knowing *why* clocks are
/// where they are is the difference between "the profile applied" and "the card is thermally
/// capped and the profile is irrelevant".
/// </summary>
[Flags]
public enum NvmlClocksEventReason : ulong
{
    None = 0,
    GpuIdle = 0x0000000000000001,
    ApplicationsClocksSetting = 0x0000000000000002,
    SwPowerCap = 0x0000000000000004,
    HwSlowdown = 0x0000000000000008,
    SyncBoost = 0x0000000000000010,
    SwThermalSlowdown = 0x0000000000000020,
    HwThermalSlowdown = 0x0000000000000040,
    HwPowerBrakeSlowdown = 0x0000000000000080,
    DisplayClockSetting = 0x0000000000000100,
}

[StructLayout(LayoutKind.Sequential)]
public struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
public struct NvmlPciInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] BusIdLegacy;
    public uint Domain;
    public uint Bus;
    public uint Device;
    public uint PciDeviceId;
    public uint PciSubSystemId;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] BusId;
}

/// <summary>Matches <c>nvmlProcessInfo_t</c> as consumed by the <c>_v3</c> process queries.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlProcessInfo
{
    public uint Pid;
    public ulong UsedGpuMemory;
    public uint GpuInstanceId;
    public uint ComputeInstanceId;
}

/// <summary>
/// Raw P/Invoke surface for nvml.dll. Callers should prefer <see cref="NvmlSession"/> and
/// <see cref="GpuDevice"/>; this type deliberately does no error handling of its own.
/// </summary>
internal static class NvmlNative
{
    const string Lib = "nvml.dll";

    /// <summary>
    /// nvml.dll lives in System32 on a normal driver install, but older/partial installs leave it
    /// only under the NVSMI directory. Probe both rather than assuming.
    /// </summary>
    static readonly string[] ProbePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     "NVIDIA Corporation", "NVSMI", "nvml.dll"),
    ];

    /// <summary>
    /// Registers the library resolver. An explicit static constructor guarantees this runs
    /// before the first P/Invoke on this type, since invoking any static member triggers the
    /// type initializer.
    /// </summary>
    static NvmlNative() =>
        NativeLibrary.SetDllImportResolver(typeof(NvmlNative).Assembly, Resolve);

    static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (!string.Equals(libraryName, Lib, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var candidate in ProbePaths)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Fall back to the default loader search order.
        return NativeLibrary.TryLoad(Lib, assembly, path, out var fallback) ? fallback : IntPtr.Zero;
    }

    // ---- lifecycle -------------------------------------------------------------------

    [DllImport(Lib)] internal static extern NvmlReturn nvmlInit_v2();
    [DllImport(Lib)] internal static extern NvmlReturn nvmlShutdown();
    [DllImport(Lib)] internal static extern IntPtr nvmlErrorString(NvmlReturn result);

    [DllImport(Lib)]
    internal static extern NvmlReturn nvmlSystemGetDriverVersion(byte[] version, uint length);

    // ---- discovery -------------------------------------------------------------------

    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(Lib, CharSet = CharSet.Ansi)]
    internal static extern NvmlReturn nvmlDeviceGetHandleByUUID(string uuid, out IntPtr device);

    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetUUID(IntPtr device, byte[] uuid, uint length);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetSerial(IntPtr device, byte[] serial, uint length);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetPciInfo_v3(IntPtr device, ref NvmlPciInfo pci);

    // ---- power -----------------------------------------------------------------------

    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMw);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limitMw);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint defaultMw);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limitMw);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport(Lib)]
    internal static extern NvmlReturn nvmlDeviceGetPowerManagementLimitConstraints(
        IntPtr device, out uint minLimitMw, out uint maxLimitMw);

    // ---- clocks ----------------------------------------------------------------------

    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceResetGpuLockedClocks(IntPtr device);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clockMhz);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetMaxClockInfo(IntPtr device, NvmlClockType type, out uint clockMhz);

    [DllImport(Lib)]
    internal static extern NvmlReturn nvmlDeviceGetSupportedMemoryClocks(IntPtr device, ref uint count, uint[]? clocksMhz);

    [DllImport(Lib)]
    internal static extern NvmlReturn nvmlDeviceGetSupportedGraphicsClocks(
        IntPtr device, uint memoryClockMhz, ref uint count, uint[]? clocksMhz);

    // ---- telemetry -------------------------------------------------------------------

    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensor, out uint tempC);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetPerformanceState(IntPtr device, out NvmlPState pState);
    [DllImport(Lib)] internal static extern NvmlReturn nvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);

    [DllImport(Lib)]
    internal static extern NvmlReturn nvmlDeviceGetComputeRunningProcesses_v3(IntPtr device, ref uint infoCount, NvmlProcessInfo[]? infos);

    [DllImport(Lib)]
    internal static extern NvmlReturn nvmlDeviceGetGraphicsRunningProcesses_v3(IntPtr device, ref uint infoCount, NvmlProcessInfo[]? infos);

    // ---- helpers ---------------------------------------------------------------------

    internal static string? TryGetErrorString(NvmlReturn code)
    {
        try
        {
            var ptr = nvmlErrorString(code);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>Decodes a NUL-terminated ASCII buffer filled by NVML.</summary>
    internal static string Decode(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
