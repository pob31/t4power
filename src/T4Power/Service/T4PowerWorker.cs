using Microsoft.Extensions.Hosting;
using T4Power.Core;
using T4Power.Core.Control;
using T4Power.Core.Ipc;
using T4Power.Core.Model;
using T4Power.Core.Nvml;

namespace T4Power.Service;

/// <summary>
/// The long-running service body. Opens NVML once, applies the right profile immediately (so a
/// headless boot is covered before anyone logs in), then ticks the rule engine and serves the
/// control pipe until stopped.
/// </summary>
internal sealed class T4PowerWorker(FileLog log) : BackgroundService
{
    GpuManager? _manager;
    IpcServer? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Paths.EnsureDataDirectory();
        log.Info("T4Power service starting");

        NvmlSession session;
        try
        {
            session = NvmlSession.Open();
        }
        catch (Exception ex) when (ex is NvmlException or DllNotFoundException)
        {
            // Without NVML there is nothing to manage. Fail loudly in the log and stop rather
            // than spinning in a retry loop that hides the problem.
            log.Error($"cannot initialise NVML: {ex.Message}");
            throw;
        }

        var store = new ConfigStore();
        _manager = new GpuManager(session, store, log);

        _manager.KickStuckPStates();
        _manager.Tick();   // apply the boot-time profile before anything else happens

        _server = new IpcServer(
            _manager.HandleAsync,
            _manager.Config.PipeAllowedSids,
            log.Warn);
        _server.Start(stoppingToken);
        log.Info($"control pipe listening on \\\\.\\pipe\\{Paths.PipeName} " +
                 $"({_manager.Config.PipeAllowedSids.Count} additional SID(s) permitted)");

        var interval = TimeSpan.FromMilliseconds(Math.Max(250, _manager.Config.PollIntervalMs));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _manager.Tick();
                }
                catch (Exception ex)
                {
                    // A single bad tick must not kill the service; the next one may well succeed
                    // (a transient NVML error, a GPU briefly reset by the driver).
                    log.Error($"tick failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        log.Info("T4Power service stopping");

        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);

        // Leave the hardware as we found it. A stopped service must not leave the GPU clamped.
        try { _manager?.RestoreAllDefaults(); }
        catch (Exception ex) { log.Error($"restoring defaults on shutdown failed: {ex.Message}"); }

        _manager?.Dispose();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        log.Info("T4Power service stopped");
    }
}
