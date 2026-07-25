using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using T4Power.Core.Ipc;
using T4Power.Core.Model;
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

    // The last values pushed in from the service. Anything equal to these came from a sync
    // rather than from the user, so there is nothing to send.
    double _syncedPowerW;
    uint _syncedClockMhz;
    bool _syncedLockEnabled;

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

    // These setters deliberately do NOT talk to the service.
    //
    // Committing from a two-way-bound setter is unfixably racy: WPF pushes bindings back through
    // them after a programmatic sync, and during a multi-property update the view can push a
    // stale value for one property while another already holds the new one. That produced
    // overrides nobody asked for — opening the window was enough to pin the GPU out of automatic
    // control, and a rule-driven profile change could commit a half-old, half-new state.
    //
    // Commits now come only from real user gestures in the view (see CommitAsync callers), which
    // is also what gives "apply on release" rather than a write per pixel of drag.

    public double PowerSliderW
    {
        get => _powerSliderW;
        set => Set(ref _powerSliderW, value);
    }

    public uint ClockSliderMhz
    {
        get => _clockSliderMhz;
        set => Set(ref _clockSliderMhz, value);
    }

    public bool ClockLockEnabled
    {
        get => _clockLockEnabled;
        set => Set(ref _clockLockEnabled, value);
    }

    /// <summary>
    /// True while the user is dragging a slider. Suspends syncing from the service so the poll
    /// loop does not yank the thumb out from under them.
    /// </summary>
    public bool IsUserAdjusting { get; set; }

    /// <summary>Sends the current slider values as a manual override. Called from the view in
    /// response to a real gesture: thumb release, key press, or a checkbox click.</summary>
    public Task<(bool Ok, string? Message)> CommitAsync()
    {
        if (MatchesSyncedState())
            return Task.FromResult((true, (string?)null));

        return _link.ApplyAdHocAsync(
            Uuid,
            SupportsPowerLimit ? _powerSliderW : null,
            // Floor and ceiling both at the slider value: a *pin*, not a ceiling. A ceiling
            // cannot raise anything - locking 300-1590 is the card's whole range and restricts
            // nothing, while an idle card still parks itself at 300 MHz. Pinning 1590 is what
            // holds it in P0, and pinning 300 is what holds it down.
            _clockLockEnabled
                ? new Core.Model.ClockLock { MinMhz = _clockSliderMhz, MaxMhz = _clockSliderMhz }
                : null,
            unlock: !_clockLockEnabled);
    }

    /// <summary>
    /// Called when the user ticks the pin box. Coming from unlocked, the slider sits at the
    /// maximum, and pinning there is usually what is wanted (it forces P0). Coming from a
    /// profile that pinned a specific clock, that value is already loaded.
    /// </summary>
    public void OnPinToggled()
    {
        if (_clockLockEnabled && _clockSliderMhz == 0)
            Set(ref _clockSliderMhz, MaxClockMhz, nameof(ClockSliderMhz));
    }

    // ---- watchlist -------------------------------------------------------------------

    /// <summary>Executables that ramp this GPU up while they run. Matched by process name, so
    /// the app is found wherever it lives — a build output or Program Files alike.</summary>
    public IReadOnlyList<string> WatchedApps =>
        _state.Rules
            .Where(r => r.Type == RuleType.ProcessName)
            .SelectMany(r => r.Match)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool HasWatchedApps => WatchedApps.Count > 0;

    string _newAppName = "";
    public string NewAppName
    {
        get => _newAppName;
        set => Set(ref _newAppName, value);
    }

    /// <summary>The profile a watchlist entry targets. Max is the point of the feature: hold
    /// the card at full clocks for as long as the app is running.</summary>
    public string WatchTargetProfile =>
        _state.Rules.FirstOrDefault(r => r.Type == RuleType.ProcessName)?.ProfileName
        ?? Core.Model.Profile.Max;

    public async Task<(bool Ok, string? Message)> AddWatchAsync()
    {
        var name = NewAppName.Trim();
        if (name.Length == 0) return (false, "type an executable name first, e.g. WFS-DIY.exe");

        var result = await _link.AddWatchAsync(Uuid, name, WatchTargetProfile).ConfigureAwait(true);
        if (result.Ok) NewAppName = "";
        return result;
    }

    public Task<(bool Ok, string? Message)> RemoveWatchAsync(string exe) =>
        _link.RemoveWatchAsync(Uuid, exe, WatchTargetProfile);

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
        // poll loop would pull the slider thumb out from under the cursor.
        if (!IsUserAdjusting) SyncSlidersFromState();

        foreach (var property in new[]
        {
            nameof(Managed), nameof(ActiveProfile), nameof(Reason), nameof(IsOverridden),
            nameof(IsThermallyGuarded), nameof(PowerText), nameof(PowerLimitText),
            nameof(ClockText), nameof(TempText), nameof(UtilText), nameof(PStateText),
            nameof(ActivityText), nameof(ThrottleText), nameof(HasThrottle),
            nameof(OverrideExpiryText), nameof(HasOverrideExpiry), nameof(ProfileNames),
            nameof(ClockLockRefused), nameof(WatchedApps), nameof(HasWatchedApps),
        })
        {
            Raise(property);
        }
    }

    void SyncSlidersFromState()
    {
        var limit = _state.Telemetry?.PowerLimitW ?? _state.DefaultPowerLimitW;
        Set(ref _powerSliderW, Math.Clamp(limit, MinPowerW, MaxPowerW), nameof(PowerSliderW));

        // The clock lock in force may come from an override *or* from the profile a rule chose.
        // Reading only the override would show "unlocked / 1590 MHz" while the card is actually
        // pinned to 300-900 MHz by Eco, which is worse than showing nothing.
        var locked = _state.Override?.LockClocks ?? ActiveProfileLock();

        Set(ref _clockLockEnabled, locked is not null, nameof(ClockLockEnabled));

        // Show the floor: that is what determines the p-state the card settles into. A profile
        // pinning 1590-1590 reads as 1590 (P0); Eco's 300-900 reads as 300 (P8).
        Set(ref _clockSliderMhz, locked?.MinMhz ?? MaxClockMhz, nameof(ClockSliderMhz));

        _syncedPowerW = _powerSliderW;
        _syncedClockMhz = _clockSliderMhz;
        _syncedLockEnabled = _clockLockEnabled;
    }

    /// <summary>True when the controls still hold exactly what the service last reported, so
    /// nothing the user did needs sending. The tolerance covers the slider's tick snapping.</summary>
    bool MatchesSyncedState() =>
        Math.Abs(_powerSliderW - _syncedPowerW) < 0.5
        && _clockSliderMhz == _syncedClockMhz
        && _clockLockEnabled == _syncedLockEnabled;

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
