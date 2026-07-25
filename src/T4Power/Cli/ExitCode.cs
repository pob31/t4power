namespace T4Power.Cli;

/// <summary>
/// Process exit codes. These are an agent- and script-facing contract: a caller must be able to
/// tell "the service is not running" apart from "you asked for something impossible" without
/// parsing prose.
/// </summary>
public static class ExitCode
{
    public const int Ok = 0;
    public const int UsageError = 1;
    public const int ServiceUnavailable = 2;
    public const int UnknownGpu = 3;
    public const int OutOfRange = 4;
    public const int PermissionDenied = 5;
    public const int NotSupported = 6;
    public const int NvmlUnavailable = 7;
    public const int Failed = 8;
}
