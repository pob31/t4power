using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using T4Power.Cli;
using T4Power.Core;
using T4Power.Core.Model;

namespace T4Power.Service;

/// <summary>
/// Registers and removes the startup service. This is the one place that needs elevation, and
/// it is deliberately a one-off: after install, the tray UI and CLI run unelevated and reach
/// the GPU through the service's pipe, so there is no recurring UAC prompt.
/// </summary>
internal static class ServiceInstaller
{
    /// <summary>
    /// Install runs in a second, elevated process launched with the runas verb, and that process
    /// gets its own console — so anything it prints is invisible to whoever started it. Mirroring
    /// to the log file is the only way a failed install leaves a trace worth reading.
    /// </summary>
    static readonly Lazy<FileLog> Log = new(() => new FileLog());

    static void Report(string message)
    {
        Console.Error.WriteLine(message);
        Log.Value.Error($"install: {message}");
    }

    static void Note(string message)
    {
        Console.WriteLine(message);
        Log.Value.Info($"install: {message}");
    }

    public static int Run(CommandLineOptions options)
    {
        // Re-launch elevated if needed. The SID recorded for the pipe ACL must be the *original*
        // user, so it is captured here, before elevation swaps the token.
        if (!IsElevated())
            return RelaunchElevated(options);

        return options.Verb == Verb.InstallService ? Install(options) : Uninstall();
    }

    static int Install(CommandLineOptions options)
    {
        if (!File.Exists(Paths.ExecutablePath))
        {
            Report($"error: cannot locate the T4Power executable at '{Paths.ExecutablePath}'");
            return ExitCode.Failed;
        }

        Paths.EnsureDataDirectory();

        // An existing service holds file locks on its own binaries, so it has to stop before we
        // can overwrite them.
        if (ServiceExists() && CurrentStatus() != ServiceControllerStatus.Stopped)
        {
            Console.WriteLine("Stopping the running service before updating its files...");
            Run("sc.exe", $"stop {Paths.ServiceName}");
            WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }

        var exe = StageBinaries();
        if (exe is null) return ExitCode.Failed;

        // Whoever asked for the install gets pipe access. Passed through --acl-sid when we
        // elevated, because by now we are running as an administrator, not as them.
        var sid = options.AclSid ?? CurrentUserSid();
        GrantPipeAccess(sid);

        if (ServiceExists())
        {
            Console.WriteLine($"'{Paths.ServiceName}' is already installed; updating its configuration.");
            Run("sc.exe", $"config {Paths.ServiceName} binPath= \"\\\"{exe}\\\" --service\" start= auto");
        }
        else
        {
            var create = Run("sc.exe",
                $"create {Paths.ServiceName} binPath= \"\\\"{exe}\\\" --service\" " +
                $"start= auto DisplayName= \"T4Power GPU Manager\" obj= LocalSystem");
            if (create != 0)
            {
                Report($"error: sc.exe create failed with exit code {create}");
                return ExitCode.Failed;
            }

            Run("sc.exe", $"description {Paths.ServiceName} " +
                          "\"Applies NVIDIA GPU power and clock profiles, automatically or on request.\"");

            // Restart on failure rather than leaving the GPU unmanaged after a transient fault.
            Run("sc.exe", $"failure {Paths.ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000");
        }

        Console.WriteLine($"Starting '{Paths.ServiceName}'...");
        Run("sc.exe", $"start {Paths.ServiceName}");

        if (!WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)))
        {
            Report($"error: the service did not report Running within 20s. Check {Paths.LogFile}");
            return ExitCode.Failed;
        }

        Console.WriteLine($"""

            T4Power is installed and running.
              service    {Paths.ServiceName} (LocalSystem, automatic start)
              config     {Paths.ConfigFile}
              log        {Paths.LogFile}
              pipe access granted to {sid}

            It will now apply your profiles at boot, before anyone logs in.
            The tray UI and CLI need no elevation from here on.
            """);

        // Motherboard fan control is optional, so this is a note rather than a failure — but it
        // is worth saying now, because the alternative is someone discovering it from a log file.
        Console.WriteLine(PawnIoProbe.IsInstalled()
            ? """

              Motherboard fan control is available (PawnIO driver detected).
              Run 'T4Power --fans' to see the headers, then --adopt-fan to drive one.
              """
            : $"""

              Note: motherboard fan control is unavailable — the PawnIO driver is not installed.
              GPU power and clock management are unaffected. To enable fan control, install
              PawnIO from {PawnIoProbe.DownloadUrl} and restart the service.
              """);

        return ExitCode.Ok;
    }

    static int Uninstall()
    {
        if (!ServiceExists())
        {
            Console.WriteLine($"'{Paths.ServiceName}' is not installed.");
            return ExitCode.Ok;
        }

        // Stopping runs the worker's StopAsync, which restores default power limits, releases
        // clock locks, and hands any adopted fan header back to the BIOS curve. Give it time to
        // finish before deleting the service — a fan left under software control by a service
        // that no longer exists would stay that way until the next reboot.
        Console.WriteLine("Stopping the service (this restores GPU defaults and hands fan headers back to the BIOS)...");
        Run("sc.exe", $"stop {Paths.ServiceName}");
        WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));

        var deleted = Run("sc.exe", $"delete {Paths.ServiceName}");
        if (deleted != 0)
        {
            Console.Error.WriteLine("error: failed to delete the service");
            return ExitCode.Failed;
        }

        Console.WriteLine($"""
            '{Paths.ServiceName}' removed. GPU defaults have been restored, and any fan header
            T4Power was driving is back under BIOS control.
            Configuration was left in place at {Paths.ConfigFile} — delete it by hand if you want a clean slate.
            """);
        return ExitCode.Ok;
    }

    /// <summary>
    /// Copies the application next to itself in Program Files and returns the installed exe
    /// path. Running the service straight out of a build directory works, but then every
    /// rebuild fails with "file in use" — and an app that lives in someone's source tree is not
    /// really installed. Already-installed copies are used in place.
    /// </summary>
    static string? StageBinaries()
    {
        if (Paths.IsRunningFromInstallDirectory) return Paths.ExecutablePath;

        var source = Path.GetDirectoryName(Paths.ExecutablePath);
        if (source is null)
        {
            Console.Error.WriteLine("error: cannot determine the source directory to install from");
            return null;
        }

        try
        {
            // Clear the target first. Upgrading a framework-dependent build to a single-file one
            // otherwise leaves the old loose DLLs and a stale deps.json/runtimeconfig.json beside
            // the new exe. Nothing user-owned lives here - config and logs are under ProgramData.
            if (Directory.Exists(Paths.InstallDirectory) && !ClearDirectory(Paths.InstallDirectory))
            {
                // "Access denied" here almost always means the image is still mapped by a running
                // process — Windows refuses to delete a running executable. Name the likely
                // culprit rather than leaving the user to guess.
                Report($"""
                    error: could not replace the files in {Paths.InstallDirectory}.

                    A T4Power process is most likely still running. Check for a tray icon and exit
                    it, then try again. If it was started elevated, it must be closed elevated:
                        taskkill /IM T4Power.exe /F
                    """);
                return null;
            }

            Directory.CreateDirectory(Paths.InstallDirectory);

            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                // Debug symbols and build leftovers are not worth shipping.
                if (Path.GetExtension(file).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(Paths.InstallDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (!WithRetry(() => File.Copy(file, target, overwrite: true), relative)) return null;
                copied++;
            }

            Note($"Installed {copied} file(s) to {Paths.InstallDirectory}");
            return Paths.InstalledExecutable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"error: could not copy files to {Paths.InstallDirectory}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Retries a file operation while the target is locked.
    ///
    /// The service control manager reports Stopped as soon as the service says so, but the
    /// process takes a moment longer to exit and release the handle on its own executable.
    /// Without this, reinstalling over a running install fails on the very first delete.
    /// </summary>
    static bool WithRetry(Action action, string what, int attempts = 20)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                {
                    Report($"error: could not replace {what} after {attempts} attempts: {ex.Message}");
                    return false;
                }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Empties a directory, waiting out any handles a just-stopped service still holds.</summary>
    static bool ClearDirectory(string path)
    {
        var ok = true;

        foreach (var file in Directory.EnumerateFiles(path))
            ok &= WithRetry(() => File.Delete(file), Path.GetFileName(file));

        foreach (var directory in Directory.EnumerateDirectories(path))
            ok &= WithRetry(() => Directory.Delete(directory, recursive: true), Path.GetFileName(directory));

        return ok;
    }

    // ---- elevation -------------------------------------------------------------------

    public static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value ?? "";

    /// <summary>
    /// Restarts this executable with the <c>runas</c> verb, which raises the single UAC prompt
    /// the whole design costs the user. The caller's SID rides along so the elevated instance
    /// can grant pipe access to the right account.
    /// </summary>
    static int RelaunchElevated(CommandLineOptions options)
    {
        var verb = options.Verb == Verb.InstallService ? "--install-service" : "--uninstall-service";
        var sid = CurrentUserSid();

        var start = new ProcessStartInfo
        {
            FileName = Paths.ExecutablePath,
            Arguments = $"{verb} --acl-sid {sid}",
            UseShellExecute = true,   // required for the runas verb
            Verb = "runas",
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                Report("error: could not start the elevated helper");
                return ExitCode.Failed;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Report("error: elevation was declined. Installing the service needs administrator rights " +
                   "once; after that, everyday use does not.");
            return ExitCode.PermissionDenied;
        }
    }

    /// <summary>Records the SID permitted to use the control pipe, alongside LocalSystem and
    /// Administrators which the server always grants.</summary>
    static void GrantPipeAccess(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return;

        var store = new ConfigStore();
        var config = store.Load();
        if (config.PipeAllowedSids.Contains(sid, StringComparer.OrdinalIgnoreCase)) return;

        store.Save(config with { PipeAllowedSids = [.. config.PipeAllowedSids, sid] });
    }

    // ---- sc.exe plumbing -------------------------------------------------------------

    static bool ServiceExists() =>
        ServiceController.GetServices().Any(s =>
            string.Equals(s.ServiceName, Paths.ServiceName, StringComparison.OrdinalIgnoreCase));

    static ServiceControllerStatus? CurrentStatus()
    {
        try
        {
            using var controller = new ServiceController(Paths.ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    static bool WaitForStatus(ServiceControllerStatus status, TimeSpan timeout)
    {
        try
        {
            using var controller = new ServiceController(Paths.ServiceName);
            controller.WaitForStatus(status, timeout);
            return controller.Status == status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    static int Run(string fileName, string arguments)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(start);
        if (process is null) return -1;

        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (output.Length > 0) Console.WriteLine(output);
        if (error.Length > 0) Console.Error.WriteLine(error);
        return process.ExitCode;
    }
}
