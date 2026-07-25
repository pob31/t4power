using T4Power.Cli;

namespace T4Power;

/// <summary>
/// Single entry point for every mode T4Power runs in. Which mode is chosen matters for
/// privilege: only <c>--service</c> and the install/uninstall verbs need elevation. Everything
/// else — the tray UI and all CLI verbs — runs as a normal user and reaches privileged
/// operations through the service over a named pipe.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            // A stack trace is never a useful answer to "set my GPU to 65 W". Log the detail and
            // give the caller one line and a meaningful exit code.
            ConsoleAttach.Ensure();
            Console.Error.WriteLine($"error: {ex.Message}");
            try { new Core.FileLog().Error($"unhandled: {ex}"); } catch (IOException) { }
            return ExitCode.Failed;
        }
    }

    static int Dispatch(string[] args)
    {
        var mode = CommandLine.Parse(args);

        switch (mode.Verb)
        {
            case Verb.TrayUi:
                return Ui.TrayHost.Run();

            case Verb.Service:
                return Service.ServiceHost.Run(args);

            case Verb.InstallService:
            case Verb.UninstallService:
                ConsoleAttach.Ensure(allocateIfMissing: true);
                return Service.ServiceInstaller.Run(mode);

            case Verb.Help:
                ConsoleAttach.Ensure();
                CommandLine.PrintUsage();
                return ExitCode.Ok;

            default:
                ConsoleAttach.Ensure();
                return CliRunner.RunAsync(mode).GetAwaiter().GetResult();
        }
    }
}
