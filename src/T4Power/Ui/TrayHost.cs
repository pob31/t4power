using System.Threading;
using T4Power.Cli;
using T4Power.Core;

namespace T4Power.Ui;

/// <summary>
/// Bootstraps the tray UI. Runs unelevated in the user's session; every privileged operation
/// goes to the service over the pipe.
/// </summary>
internal static class TrayHost
{
    /// <param name="showWindow">Open the window immediately. False when launched at login,
    /// where appearing only in the tray is the polite behaviour.</param>
    public static int Run(bool showWindow = false)
    {
        // A second tray icon would be confusing and would double the poll traffic.
        using var single = new Mutex(true, @"Local\T4Power.TrayUi", out var isFirst);
        if (!isFirst)
        {
            ConsoleAttach.Ensure();
            Console.Error.WriteLine("T4Power is already running — see the notification area.");
            return ExitCode.Ok;
        }

        var app = new App();
        var model = new MainViewModel();
        var link = new ServiceLink();

        MainWindow? window = null;
        MainWindow GetWindow() => window ??= new MainWindow(model, link);

        using var tray = new TrayIcon(link, GetWindow);

        link.StateChanged += states => model.Update(states, link);
        link.AvailabilityChanged += (available, reason) =>
        {
            model.ServiceAvailable = available;
            model.ServiceStatus = available
                ? "Connected to the T4Power service"
                : $"Service unavailable — {reason ?? "not running"}";

            if (!available) model.Gpus.Clear();
        };

        link.Start();

        app.Exit += (_, _) =>
        {
            link.Dispose();
            tray.Dispose();
        };

        // No StartupUri: the window is created on demand so launching at login only puts an
        // icon in the tray rather than popping a window in the user's face.
        if (showWindow) GetWindow().Show();

        return app.Run();
    }
}
