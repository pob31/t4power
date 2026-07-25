using T4Power.Cli;

namespace T4Power.Ui;

/// <summary>Bootstraps the tray UI. Runs unelevated; all privileged work goes via the service.</summary>
internal static class TrayHost
{
    public static int Run()
    {
        // TODO(ui): tray icon, per-GPU cards, sliders and rule editor.
        ConsoleAttach.Ensure(allocateIfMissing: true);
        Console.Error.WriteLine("The tray UI is not built yet. Use --list, --status or --help.");
        return ExitCode.Failed;
    }
}
