using System.Windows.Threading;
using T4Power.Core.Ipc;
using T4Power.Core.Model;

namespace T4Power.Ui;

/// <summary>
/// The UI's connection to the service. Polls state on a timer and pushes commands through the
/// pipe — the UI never touches NVML itself, which is what keeps it unelevated.
/// </summary>
internal sealed class ServiceLink : IDisposable
{
    readonly IpcClient _client = new(timeoutMs: 1500);
    readonly DispatcherTimer _timer;

    bool _busy;

    public ServiceLink(TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Raised on the UI thread whenever fresh state arrives.</summary>
    public event Action<IReadOnlyList<GpuStateDto>>? StateChanged;

    /// <summary>Raised when the service becomes reachable or unreachable, with the reason.</summary>
    public event Action<bool, string?>? AvailabilityChanged;

    public bool IsAvailable { get; private set; }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    public async Task RefreshAsync()
    {
        // Skip rather than queue: a slow round-trip must not pile up timer ticks.
        if (_busy) return;
        _busy = true;

        try
        {
            var response = await _client.SendAsync(new IpcRequest { Command = IpcCommands.GetState })
                .ConfigureAwait(true);

            SetAvailability(response.Ok, response.Error);
            if (response.Ok) StateChanged?.Invoke(response.Gpus);
        }
        catch (Exception ex) when (ex is IpcClient.UnavailableException or IpcClient.AccessDeniedException or IOException)
        {
            SetAvailability(false, ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Sends a command and refreshes immediately, so the UI reflects the result at once.</summary>
    public async Task<(bool Ok, string? Message)> SendAsync(IpcRequest request)
    {
        try
        {
            var response = await _client.SendAsync(request).ConfigureAwait(true);
            SetAvailability(true, null);

            if (response.Ok && response.Gpus.Count > 0) StateChanged?.Invoke(response.Gpus);
            else if (response.Ok) await RefreshAsync().ConfigureAwait(true);

            return (response.Ok, response.Ok ? response.Message : response.Error);
        }
        catch (Exception ex) when (ex is IpcClient.UnavailableException or IpcClient.AccessDeniedException or IOException)
        {
            SetAvailability(false, ex.Message);
            return (false, ex.Message);
        }
    }

    public Task<(bool Ok, string? Message)> ApplyProfileAsync(string uuid, string profile, TimeSpan? duration = null) =>
        SendAsync(new IpcRequest
        {
            Command = IpcCommands.SetOverride,
            Gpu = uuid,
            Profile = profile,
            DurationSeconds = duration is { } d ? (int)d.TotalSeconds : null,
        });

    public Task<(bool Ok, string? Message)> ApplyAdHocAsync(
        string uuid, double? powerW, ClockLock? clocks, bool unlock, TimeSpan? duration = null) =>
        SendAsync(new IpcRequest
        {
            Command = IpcCommands.SetOverride,
            Gpu = uuid,
            PowerLimitW = powerW,
            LockClocks = clocks,
            UnlockClocks = unlock,
            DurationSeconds = duration is { } d ? (int)d.TotalSeconds : null,
        });

    public Task<(bool Ok, string? Message)> ReturnToAutoAsync(string uuid) =>
        SendAsync(new IpcRequest { Command = IpcCommands.ClearOverride, Gpu = uuid });

    public Task<(bool Ok, string? Message)> RestoreDefaultsAsync(string uuid) =>
        SendAsync(new IpcRequest { Command = IpcCommands.RestoreDefaults, Gpu = uuid });

    public Task<(bool Ok, string? Message)> AddWatchAsync(string uuid, string exe, string profile) =>
        SendAsync(new IpcRequest
        {
            Command = IpcCommands.AddWatch,
            Gpu = uuid,
            Profile = profile,
            Match = [exe],
        });

    public Task<(bool Ok, string? Message)> RemoveWatchAsync(string uuid, string exe, string profile) =>
        SendAsync(new IpcRequest
        {
            Command = IpcCommands.RemoveWatch,
            Gpu = uuid,
            Profile = profile,
            Match = [exe],
        });

    public Task<(bool Ok, string? Message)> SaveConfigAsync(GpuConfig config) =>
        SendAsync(new IpcRequest { Command = IpcCommands.SetGpuConfig, GpuConfig = config });

    void SetAvailability(bool available, string? reason)
    {
        if (IsAvailable == available) return;
        IsAvailable = available;
        AvailabilityChanged?.Invoke(available, reason);
    }

    public void Dispose() => _timer.Stop();
}
