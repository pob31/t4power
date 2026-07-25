namespace T4Power.Core;

/// <summary>
/// Well-known locations. Everything lives under ProgramData rather than a per-user folder
/// because the service runs as LocalSystem and must see the same config the UI is editing.
/// </summary>
public static class Paths
{
    public const string ServiceName = "T4Power";
    public const string PipeName = "T4Power";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "T4Power");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string LogFile => Path.Combine(LogDirectory, "t4power.log");

    /// <summary>Full path to the running executable.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()!.Location;

    /// <summary>
    /// Where the service runs from. Install copies the binaries here and registers this
    /// location, so the service never holds a lock on a build output directory — otherwise
    /// rebuilding while the service is running fails with a file-in-use error.
    /// </summary>
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "T4Power");

    public static string InstalledExecutable => Path.Combine(InstallDirectory, "T4Power.exe");

    /// <summary>True when the running process is the installed copy rather than a build output.</summary>
    public static bool IsRunningFromInstallDirectory =>
        Path.GetDirectoryName(ExecutablePath)?.TrimEnd('\\')
            .Equals(InstallDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true;

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
