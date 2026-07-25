using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using T4Power.Cli;
using T4Power.Core;

namespace T4Power.Service;

/// <summary>
/// Hosts <see cref="T4PowerWorker"/>. Runs as a Windows Service under the SCM, or in the
/// foreground when launched from a console, which is what makes the service debuggable without
/// installing it.
/// </summary>
internal static class ServiceHost
{
    public static int Run(string[] args)
    {
        _ = args;
        var interactive = !WindowsServiceHelpers.IsWindowsService();
        if (interactive) ConsoleAttach.Ensure(allocateIfMissing: true);

        var log = new FileLog(alsoConsole: interactive);

        try
        {
            // Deliberately not forwarding args: the command-line configuration provider rejects
            // a bare "--service" (it expects --key=value pairs) and we take no host settings
            // from the command line anyway.
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton(log);
            builder.Services.AddHostedService<T4PowerWorker>();

            // No-op when running interactively, so the same path serves both modes.
            builder.Services.AddWindowsService(options => options.ServiceName = Paths.ServiceName);

            builder.Build().Run();
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            log.Error($"service terminated: {ex}");
            if (interactive) Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCode.Failed;
        }
    }
}
