using System.ServiceProcess;
using T4Power.Core;

namespace T4Power.Service;

/// <summary>
/// Finds and starts PawnIO, the signed kernel driver that motherboard fan control depends on.
///
/// Some history, because the choice is not obvious. Reaching a SuperIO chip means talking to
/// legacy I/O ports, which needs a kernel driver. The traditional one is WinRing0, and it cannot
/// load on a machine with Memory Integrity (HVCI) enabled — it is on Microsoft's vulnerable-driver
/// blocklist. LibreHardwareMonitor moved to PawnIO for exactly that reason, so on a modern secured
/// Windows install PawnIO is not one option among several, it is the only one.
///
/// Two things about it drive this class:
///
/// 1. It is a *kernel driver*, not a Win32 service. <see cref="ServiceController.GetServices()"/>
///    does not list it; <see cref="ServiceController.GetDevices()"/> does.
/// 2. Its start type is Manual. Something has to start it, and if T4Power is the only fan app on
///    the machine then that something is T4Power — otherwise fan control would work until the
///    first reboot and then quietly stop.
/// </summary>
public static class PawnIoProbe
{
    public const string DriverName = "PawnIO";
    public const string DownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases";

    public static string NotInstalledMessage =>
        "the PawnIO driver is not installed, so motherboard fan headers cannot be reached. " +
        "It is required on any machine with Memory Integrity (HVCI) enabled, which is most of " +
        $"them. Install it from {DownloadUrl} and restart the T4Power service.";

    /// <summary>
    /// Whether the driver is installed at all. Used by the installer to tell someone up front
    /// that fan control will not work, rather than letting them find out from a log file.
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            var devices = ServiceController.GetDevices();
            try
            {
                return devices.Any(d => string.Equals(d.ServiceName, DriverName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                foreach (var device in devices) device.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Makes sure the driver is present and running. Returns null on success, or a message
    /// explaining what a user should do about it.
    ///
    /// Never throws: fan control is additive, and a machine without it must still get full GPU
    /// management.
    /// </summary>
    public static string? EnsureRunning(FileLog log)
    {
        ServiceController[] devices;
        try
        {
            devices = ServiceController.GetDevices();
        }
        catch (Exception ex)
        {
            return $"could not enumerate kernel drivers to find PawnIO: {ex.Message}";
        }

        try
        {
            var driver = devices.FirstOrDefault(d =>
                string.Equals(d.ServiceName, DriverName, StringComparison.OrdinalIgnoreCase));

            if (driver is null) return NotInstalledMessage;

            driver.Refresh();
            if (driver.Status == ServiceControllerStatus.Running) return null;

            log.Info($"the PawnIO driver is installed but {driver.Status}; starting it");

            try
            {
                if (driver.Status is not (ServiceControllerStatus.StartPending
                                       or ServiceControllerStatus.ContinuePending))
                {
                    driver.Start();
                }

                driver.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                log.Info("the PawnIO driver is running");
                return null;
            }
            catch (Exception ex)
            {
                return $"the PawnIO driver is installed but could not be started ({ex.Message}). " +
                       "Motherboard fan control is unavailable until it runs.";
            }
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }
}
