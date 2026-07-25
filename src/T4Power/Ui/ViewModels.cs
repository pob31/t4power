using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using T4Power.Core.Ipc;
using T4Power.Core.Rules;

namespace T4Power.Ui;

internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

internal sealed class MainViewModel : ObservableObject
{
    string _serviceStatus = "Connecting to the T4Power service...";
    bool _serviceAvailable;
    string? _lastMessage;

    public ObservableCollection<GpuViewModel> Gpus { get; } = [];

    public string ServiceStatus { get => _serviceStatus; set => Set(ref _serviceStatus, value); }
    public bool ServiceAvailable { get => _serviceAvailable; set => Set(ref _serviceAvailable, value); }
    public string? LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }

    /// <summary>Merges fresh state in place so the UI does not lose slider focus or scroll position.</summary>
    public void Update(IReadOnlyList<GpuStateDto> states, ServiceLink link)
    {
        // Managed GPUs first: the card T4Power actually controls is the one worth seeing without
        // scrolling. Stable ordering beyond that, so cards never shuffle under the cursor.
        var ordered = states
            .OrderByDescending(s => s.Managed)
            .ThenBy(s => s.Index)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var state = ordered[i];
            var existing = Gpus.FirstOrDefault(g => g.Uuid == state.Uuid);

            if (existing is null)
            {
                Gpus.Insert(Math.Min(i, Gpus.Count), new GpuViewModel(state, link));
                continue;
            }

            existing.Update(state);

            var at = Gpus.IndexOf(existing);
            if (at != i && i < Gpus.Count) Gpus.Move(at, i);
        }

        // Drop GPUs the service no longer reports (driver reload, card removed).
        foreach (var gone in Gpus.Where(g => ordered.All(s => s.Uuid != g.Uuid)).ToList())
            Gpus.Remove(gone);
    }
}

internal sealed class GpuViewModel : ObservableObject
{
    readonly ServiceLink _link;

    GpuStateDto _state;
    double _powerSliderW;
    uint _clockSliderMhz;
    bool _clockLockEnabled;
    bool _suppressCommit;

    public GpuViewModel(GpuStateDto state, ServiceLink link)
    {
        _state = state;
        _link = link;
        SyncSlidersFromState();
    }

    public string Uuid => _state.Uuid;
    public string Name => _state.Name;
    public string Subtitle => $"{_state.PciBusId}   {_state.Uuid}";
    public bool Managed => _state.Managed;

    public double MinPowerW => _state.MinPowerLimitW;
    public double MaxPowerW => _state.MaxPowerLimitW;
    public double DefaultPowerW => _state.DefaultPowerLimitW;
    public uint MinClockMhz => _state.MinGraphicsClockMhz;
    public uint MaxClockMhz => _state.MaxGraphicsClockMhz;

    public bool SupportsPowerLimit => _state.SupportsPowerLimit;
    public bool PowerRangeIsUseful => _state.MaxPowerLimitW - _state.MinPowerLimitW >= 1;

    public IReadOnlyList<string> ProfileNames => _state.Profiles.Select(p => p.Name).ToList();
    public string? ActiveProfile => _state.ActiveProfile;
    public string Reason => _state.Reason ?? "";

    public bool IsOverridden => _state.Source == DecisionSource.Override;
    public bool IsThermallyGuarded => _state.Source == DecisionSource.ThermalGuard;

    /// <summary>Shown next to the clock slider when the GPU has refused a clock lock.</summary>
    public bool ClockLockRefused => _state.LockedClocksSupported == false;

    // --- live telemetry ---
    public string PowerText => _state.Telemetry?.PowerDrawW is { } w ? $"{w:0.0} W" : "--";
    public string PowerLimitText => _state.Telemetry?.PowerLimitW is { } w ? $"{w:0.#} W cap" : "";
    public string ClockText => _state.Telemetry?.SmClockMhz is { } c ? $"{c} MHz" : "--";
    public string TempText => _state.Telemetry?.TemperatureC is { } t ? $"{t} °C" : "--";
    public string UtilText => _state.Telemetry?.GpuUtilPercent is { } u ? $"{u}%" : "--";
    public string PStateText => _state.Telemetry?.PState?.ToString() ?? "--";

    public string ActivityText => _state.Telemetry?.ActivePids.Count switch
    {
        null or 0 => "no GPU contexts",
        1 => "1 process using this GPU",
        var n => $"{n} processes using this GPU",
    };

    public string ThrottleText => string.Join(", ", _state.Telemetry?.InterestingReasons() ?? []);
    public bool HasThrottle => ThrottleText.Length > 0;

    public string OverrideExpiryText => _state.Override?.ExpiresUtc is { } exp
        ? $"reverts in {Humanise(exp - DateTimeOffset.UtcNow)}"
        : "";

    public bool HasOverrideExpiry => _state.Override?.ExpiresUtc is not null;

    // --- sliders ---

    public double PowerSliderW
    {
        get => _powerSliderW;
        set { if (Set(ref _powerSliderW, value)) Commit(); }
    }

    public uint ClockSliderMhz
    {
        get => _clockSliderMhz;
        set { if (Set(ref _clockSliderMhz, value)) Commit(); }
    }

    public bool ClockLockEnabled
    {
        get => _clockLockEnabled;
        set { if (Set(ref _clockLockEnabled, value)) Commit(); }
    }

    /// <summary>
    /// Debounces slider input. Dragging raises a change per pixel; without this the service
    /// would take a burst of NVML writes for a single gesture.
    /// </summary>
    void Commit()
    {
        if (_suppressCommit) return;
        Debounce.Run(Uuid, TimeSpan.FromMilliseconds(300), async () =>
        {
            await _link.ApplyAdHocAsync(
                Uuid,
                SupportsPowerLimit ? _powerSliderW : null,
                _clockLockEnabled ? new Core.Model.ClockLock { MinMhz = MinClockMhz, MaxMhz = _clockSliderMhz } : null,
                unlock: !_clockLockEnabled).ConfigureAwait(true);
        });
    }

    public Task<(bool Ok, string? Message)> ApplyProfileAsync(string profile) =>
        _link.ApplyProfileAsync(Uuid, profile);

    public Task<(bool Ok, string? Message)> ReturnToAutoAsync() => _link.ReturnToAutoAsync(Uuid);

    public Task<(bool Ok, string? Message)> RestoreDefaultsAsync() => _link.RestoreDefaultsAsync(Uuid);

    public Task<(bool Ok, string? Message)> BoostAsync(TimeSpan duration) =>
        _link.ApplyProfileAsync(Uuid, Core.Model.Profile.Max, duration);

    public void Update(GpuStateDto state)
    {
        _state = state;

        // Only track the service's values when the user is not mid-adjustment, otherwise the
        // slider would fight the poll loop.
        if (!Debounce.IsPending(Uuid)) SyncSlidersFromState();

        foreach (var property in new[]
        {
            nameof(Managed), nameof(ActiveProfile), nameof(Reason), nameof(IsOverridden),
            nameof(IsThermallyGuarded), nameof(PowerText), nameof(PowerLimitText),
            nameof(ClockText), nameof(TempText), nameof(UtilText), nameof(PStateText),
            nameof(ActivityText), nameof(ThrottleText), nameof(HasThrottle),
            nameof(OverrideExpiryText), nameof(HasOverrideExpiry), nameof(ProfileNames),
            nameof(ClockLockRefused),
        })
        {
            Raise(property);
        }
    }

    void SyncSlidersFromState()
    {
        _suppressCommit = true;

        var limit = _state.Telemetry?.PowerLimitW ?? _state.DefaultPowerLimitW;
        Set(ref _powerSliderW, Math.Clamp(limit, MinPowerW, MaxPowerW), nameof(PowerSliderW));

        // The clock lock in force may come from an override *or* from the profile a rule chose.
        // Reading only the override would show "unlocked / 1590 MHz" while the card is actually
        // pinned to 300-900 MHz by Eco, which is worse than showing nothing.
        var locked = _state.Override?.LockClocks ?? ActiveProfileLock();

        Set(ref _clockLockEnabled, locked is not null, nameof(ClockLockEnabled));
        Set(ref _clockSliderMhz, locked?.MaxMhz ?? MaxClockMhz, nameof(ClockSliderMhz));

        _suppressCommit = false;
    }

    Core.Model.ClockLock? ActiveProfileLock() =>
        _state.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, _state.ActiveProfile, StringComparison.OrdinalIgnoreCase))?.LockClocks;

    static string Humanise(TimeSpan span) => span switch
    {
        { TotalSeconds: < 1 } => "moments",
        { TotalMinutes: < 1 } => $"{span.TotalSeconds:0}s",
        { TotalHours: < 1 } => $"{span.TotalMinutes:0}m",
        _ => $"{span.TotalHours:0.#}h",
    };
}

/// <summary>Coalesces rapid repeated calls per key, so a slider drag results in one write.</summary>
internal static class Debounce
{
    static readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> Timers = [];

    public static bool IsPending(string key) => Timers.ContainsKey(key);

    public static void Run(string key, TimeSpan delay, Func<Task> action)
    {
        if (Timers.TryGetValue(key, out var existing)) existing.Stop();

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            Timers.Remove(key);
            await action().ConfigureAwait(true);
        };

        Timers[key] = timer;
        timer.Start();
    }
}
