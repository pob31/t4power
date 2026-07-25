namespace T4Power.Core.Nvml;

/// <summary>Return codes from the NVML API (nvml.h, <c>nvmlReturn_t</c>).</summary>
public enum NvmlReturn
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    IrqIssue = 11,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    CorruptedInfoRom = 14,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibRmVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    VgpuEccNotSupported = 22,
    InsufficientResources = 23,
    FreqNotSupported = 24,
    ArgumentVersionMismatch = 25,
    DeprecatedFunction = 26,
    Unknown = 999,
}

public sealed class NvmlException : Exception
{
    public NvmlReturn Code { get; }

    /// <summary>The NVML call that failed, e.g. <c>nvmlDeviceSetPowerManagementLimit</c>.</summary>
    public string Operation { get; }

    public NvmlException(NvmlReturn code, string operation, string? detail = null)
        : base(BuildMessage(code, operation, detail))
    {
        Code = code;
        Operation = operation;
    }

    /// <summary>
    /// True when the call failed only because the caller is not elevated. The service runs as
    /// LocalSystem so this should never surface there; if it does, the UI needs to say so
    /// plainly rather than showing a generic failure.
    /// </summary>
    public bool IsPermissionDenied => Code == NvmlReturn.NoPermission;

    /// <summary>True when this GPU or driver simply does not offer the feature.</summary>
    public bool IsNotSupported => Code is NvmlReturn.NotSupported or NvmlReturn.DeprecatedFunction;

    static string BuildMessage(NvmlReturn code, string operation, string? detail)
    {
        var text = NvmlNative.TryGetErrorString(code) ?? code.ToString();
        var msg = $"{operation} failed: {text} ({(int)code})";
        if (code == NvmlReturn.NoPermission)
            msg += " — this operation requires elevation; it is normally performed by the T4Power service.";
        return detail is null ? msg : $"{msg} [{detail}]";
    }
}
