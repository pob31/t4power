using T4Power.Core.Ipc;
using T4Power.Core.Model;
using T4Power.Core.Nvml;
using T4Power.Core.Rules;

namespace T4Power.Core.Control;

/// <summary>
/// The service's brain, and the only component in the system that writes to NVML.
///
/// It owns the NVML session, the config, and the rule engine; every tick it reads telemetry,
/// asks the engine what the GPU should be doing, and applies the difference. Clients never
/// reach past this class — they send requests, which are validated here before anything is
/// written to hardware.
/// </summary>
public sealed class GpuManager : IDisposable
{
    /// <summary>What we last wrote to a GPU, so unchanged state is not rewritten every second.</summary>
    sealed record AppliedState
    {
        public double? PowerLimitW { get; init; }
        public ClockLock? LockClocks { get; init; }
        public bool ClocksUnlocked { get; init; }
        public string? ProfileName { get; init; }
    }

    readonly NvmlSession _session;
    readonly ConfigStore _store;
    readonly RuleEngine _engine;
    readonly IProcessSource _processes;
    readonly FileLog _log;
    readonly Func<DateTimeOffset> _clock;
    readonly object _gate = new();

    readonly Dictionary<string, AppliedState> _applied = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, GpuTelemetry> _lastTelemetry = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, RuleDecision> _lastDecision = new(StringComparer.OrdinalIgnoreCase);

    AppConfig _config;

    public GpuManager(
        NvmlSession session,
        ConfigStore store,
        FileLog log,
        IProcessSource? processes = null,
        Func<DateTimeOffset>? clock = null)
    {
        _session = session;
        _store = store;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _engine = new RuleEngine(_clock);
        _processes = processes ?? new ProcessWatcher(clock: _clock);

        // Discovery is by UUID, so a GPU that was moved to another slot keeps its settings and a
        // newly seen one picks up defaults.
        _config = _store.Load().EnsureEntriesFor(_session.Devices.Select(d => d.Info));
        _store.Save(_config);

        _log.Info($"NVML {_session.DriverVersion}, {_session.Devices.Count} GPU(s): " +
                  string.Join(", ", _session.Devices.Select(d => $"{d.Info.Name} [{d.Info.Uuid}]")));
    }

    public AppConfig Config { get { lock (_gate) return _config; } }
    public string? DriverVersion => _session.DriverVersion;

    // ---- startup / shutdown ----------------------------------------------------------

    /// <summary>
    /// Unwedges a GPU stuck at P0. A T4 can sit at P0/1590 MHz drawing 36 W with nothing
    /// running; a lock-then-unlock cycle drops it to P8/300 MHz and ~10 W. Measured on driver
    /// 610.74. Harmless on cards that idle correctly, so it just runs where it applies.
    /// </summary>
    public void KickStuckPStates()
    {
        if (!_config.KickStuckPState) return;

        foreach (var device in ManagedDevices())
        {
            var telemetry = device.ReadTelemetry();
            var wedged = telemetry.PState == NvmlPState.P0
                         && !telemetry.HasActiveContext
                         && telemetry.GpuUtilPercent is null or 0;
            if (!wedged) continue;

            try
            {
                var min = device.Info.MinGraphicsClockMhz;
                if (min == 0) continue;

                _log.Info($"{device.Info.Name} is idle but pinned at P0 ({telemetry.PowerDrawW:0.#} W, " +
                          $"{telemetry.SmClockMhz} MHz); cycling the clock lock to release it");

                device.SetLockedClocks(min, min);
                Thread.Sleep(250);
                device.ResetLockedClocks();

                // The normal profile is applied straight after this, so leaving clocks unlocked
                // here is fine.
                _applied.Remove(device.Uuid);
            }
            catch (NvmlException ex)
            {
                _log.Warn($"P-state kick failed for {device.Info.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Puts every managed GPU back to its default power limit with clocks unlocked. Called on
    /// service stop and uninstall so the machine is never left clamped by an app that is no
    /// longer running.
    /// </summary>
    public void RestoreAllDefaults()
    {
        foreach (var device in ManagedDevices())
        {
            _log.Info($"restoring {device.Info.Name} to defaults " +
                      $"({device.Info.DefaultPowerLimitW:0.#} W, clocks unlocked)");
            device.RestoreDefaults();
            _applied.Remove(device.Uuid);
        }
    }

    // ---- the tick --------------------------------------------------------------------

    /// <summary>Reads telemetry, evaluates rules, and applies any change. Called once per poll.</summary>
    public void Tick()
    {
        var running = _processes.GetRunningNames();
        var now = _clock();

        foreach (var device in _session.Devices)
        {
            GpuConfig? gpuConfig;
            lock (_gate) gpuConfig = _config.Find(device.Uuid);
            if (gpuConfig is null) continue;

            GpuTelemetry telemetry;
            try
            {
                telemetry = device.ReadTelemetry();
            }
            catch (NvmlException ex)
            {
                _log.Warn($"telemetry read failed for {device.Info.Name}: {ex.Message}");
                continue;
            }

            lock (_gate) _lastTelemetry[device.Uuid] = telemetry;

            // Drop an expired override before evaluating, so the change is persisted rather than
            // silently re-derived every tick.
            if (gpuConfig.Override?.IsExpired(now) == true)
            {
                _log.Info($"{device.Info.Name}: override expired, returning to automatic control");
                gpuConfig = gpuConfig with { Override = null };
                UpdateGpuConfig(gpuConfig);
            }

            var decision = _engine.Evaluate(gpuConfig, telemetry, running);
            lock (_gate) _lastDecision[device.Uuid] = decision;

            if (decision.Desired is not null)
                ApplyIfChanged(device, decision);
        }
    }

    void ApplyIfChanged(GpuDevice device, RuleDecision decision)
    {
        var desired = decision.Desired!;
        _applied.TryGetValue(device.Uuid, out var current);

        var wantPower = desired.PowerLimitW is { } w ? device.Info.ClampPowerW(w) : (double?)null;
        var powerChanged = wantPower is not null &&
                           (current?.PowerLimitW is not { } had || Math.Abs(had - wantPower.Value) > 0.01);

        var clocksChanged = desired.LockClocks is { } want
            ? current?.LockClocks is not { } have || have.MinMhz != want.MinMhz || have.MaxMhz != want.MaxMhz
            : desired.UnlockClocks && current is not { ClocksUnlocked: true };

        if (!powerChanged && !clocksChanged) return;

        var applied = current ?? new AppliedState();

        if (powerChanged)
        {
            try
            {
                var actual = device.SetPowerLimitW(wantPower!.Value);
                applied = applied with { PowerLimitW = actual };
                _log.Info($"{device.Info.Name}: power limit -> {actual:0.#} W ({decision.Reason})");
            }
            catch (NvmlException ex)
            {
                _log.Error($"{device.Info.Name}: setting power limit failed: {ex.Message}");
            }
        }

        if (clocksChanged)
        {
            try
            {
                if (desired.LockClocks is { } lockTo)
                {
                    var (min, max) = device.SetLockedClocks(lockTo.MinMhz, lockTo.MaxMhz);
                    applied = applied with
                    {
                        LockClocks = new ClockLock { MinMhz = min, MaxMhz = max },
                        ClocksUnlocked = false,
                    };
                    _log.Info($"{device.Info.Name}: clocks locked to {min}-{max} MHz ({decision.Reason})");
                }
                else
                {
                    device.ResetLockedClocks();
                    applied = applied with { LockClocks = null, ClocksUnlocked = true };
                    _log.Info($"{device.Info.Name}: clocks unlocked ({decision.Reason})");
                }
            }
            catch (NvmlException ex)
            {
                _log.Error($"{device.Info.Name}: setting clocks failed: {ex.Message}");
            }
        }

        _applied[device.Uuid] = applied with { ProfileName = desired.ProfileName };
    }

    // ---- IPC handling ----------------------------------------------------------------

    /// <summary>
    /// Handles one client request. Everything arriving here is untrusted: values are clamped to
    /// live NVML constraints and unknown selectors are rejected rather than applied broadly.
    /// </summary>
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken token)
    {
        _ = token;
        try
        {
            return Task.FromResult(Handle(request));
        }
        catch (NvmlException ex)
        {
            return Task.FromResult(IpcResponse.Failure(ex.Message,
                ex.IsPermissionDenied ? 5 : ex.IsNotSupported ? 6 : 8));
        }
    }

    IpcResponse Handle(IpcRequest request) => request.Command switch
    {
        IpcCommands.Ping => new IpcResponse { Ok = true, ServiceVersion = Version, DriverVersion = DriverVersion },
        IpcCommands.GetState or IpcCommands.GetConfig => BuildState(request.Gpu),
        IpcCommands.SetOverride => SetOverride(request),
        IpcCommands.ClearOverride => ClearOverride(request),
        IpcCommands.RestoreDefaults => RestoreDefaults(request),
        IpcCommands.SetGpuConfig => SetGpuConfig(request),
        _ => IpcResponse.Failure($"unknown command '{request.Command}'", 1),
    };

    IpcResponse BuildState(string? selector)
    {
        var devices = Select(selector, out var error);
        if (error is not null) return error;

        lock (_gate)
        {
            return new IpcResponse
            {
                Ok = true,
                ServiceVersion = Version,
                DriverVersion = DriverVersion,
                Gpus = devices.Select(d =>
                {
                    var cfg = _config.Find(d.Uuid);
                    _lastTelemetry.TryGetValue(d.Uuid, out var telemetry);
                    _lastDecision.TryGetValue(d.Uuid, out var decision);

                    return new GpuStateDto
                    {
                        Uuid = d.Info.Uuid,
                        Name = d.Info.Name,
                        Index = d.Info.Index,
                        PciBusId = d.Info.PciBusId,
                        Managed = cfg?.Managed ?? false,
                        MinPowerLimitW = d.Info.MinPowerLimitW,
                        MaxPowerLimitW = d.Info.MaxPowerLimitW,
                        DefaultPowerLimitW = d.Info.DefaultPowerLimitW,
                        SupportsPowerLimit = d.Info.SupportsPowerLimit,
                        MinGraphicsClockMhz = d.Info.MinGraphicsClockMhz,
                        MaxGraphicsClockMhz = d.Info.MaxGraphicsClockMhz,
                        Telemetry = telemetry,
                        ActiveProfile = decision?.Desired?.ProfileName,
                        Reason = decision?.Reason,
                        Source = decision?.Source ?? DecisionSource.Default,
                        Override = cfg?.Override,
                        LockedClocksSupported = d.LockedClocksSupported,
                        Profiles = cfg?.Profiles ?? [],
                        Rules = cfg?.Rules ?? [],
                        ThermalGuardC = cfg?.ThermalGuardC,
                    };
                }).ToList(),
            };
        }
    }

    IpcResponse SetOverride(IpcRequest request)
    {
        var devices = Select(request.Gpu, out var error);
        if (error is not null) return error;

        var expires = request.DurationSeconds is { } seconds and > 0
            ? _clock().AddSeconds(seconds)
            : (DateTimeOffset?)null;

        var notes = new List<string>();

        foreach (var device in devices)
        {
            var cfg = _config.Find(device.Uuid);
            if (cfg is null) continue;

            if (!cfg.Managed)
            {
                notes.Add($"{device.Info.Name} is not managed; skipped");
                continue;
            }

            if (request.Profile is not null && cfg.FindProfile(request.Profile) is null)
            {
                return IpcResponse.Failure(
                    $"{device.Info.Name} has no profile '{request.Profile}' " +
                    $"(available: {string.Join(", ", cfg.Profiles.Select(p => p.Name))})", 1);
            }

            // Clamp against what this specific GPU reports, not against anything the client said.
            double? power = null;
            if (request.PowerLimitW is { } requested)
            {
                power = device.Info.ClampPowerW(requested);
                if (Math.Abs(power.Value - requested) > 0.01)
                {
                    notes.Add($"{requested:0.#} W is outside {device.Info.Name}'s " +
                              $"{device.Info.MinPowerLimitW:0.#}-{device.Info.MaxPowerLimitW:0.#} W range; " +
                              $"clamped to {power:0.#} W");
                }
            }

            ClockLock? clocks = null;
            if (request.LockClocks is { } wanted)
            {
                var min = device.Info.SnapClock(wanted.MinMhz);
                var max = device.Info.SnapClock(wanted.MaxMhz);
                if (min > max) (min, max) = (max, min);
                clocks = new ClockLock { MinMhz = min, MaxMhz = max };

                if (min != wanted.MinMhz || max != wanted.MaxMhz)
                    notes.Add($"clocks snapped to supported steps {min}-{max} MHz");
            }

            UpdateGpuConfig(cfg with
            {
                Override = new Override
                {
                    ProfileName = request.Profile,
                    PowerLimitW = power,
                    LockClocks = clocks,
                    UnlockClocks = request.UnlockClocks,
                    ExpiresUtc = expires,
                },
            });

            var ttl = expires is null ? "until cleared" : $"for {request.DurationSeconds}s";
            var what = request.Profile ?? $"{power:0.#} W";
            notes.Add($"{device.Info.Name} -> {what} {ttl}");
            _log.Info($"override set: {device.Info.Name} -> {what} {ttl}");
        }

        // Apply immediately so the caller sees the effect without waiting for the next tick.
        Tick();
        return new IpcResponse { Ok = true, Message = string.Join("; ", notes), Gpus = BuildState(request.Gpu).Gpus };
    }

    IpcResponse ClearOverride(IpcRequest request)
    {
        var devices = Select(request.Gpu, out var error);
        if (error is not null) return error;

        foreach (var device in devices)
        {
            var cfg = _config.Find(device.Uuid);
            if (cfg?.Override is null) continue;
            UpdateGpuConfig(cfg with { Override = null });
            _engine.Reset(device.Uuid);
            _log.Info($"override cleared: {device.Info.Name} back to automatic control");
        }

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = "returned to automatic control",
            Gpus = BuildState(request.Gpu).Gpus,
        };
    }

    IpcResponse RestoreDefaults(IpcRequest request)
    {
        var devices = Select(request.Gpu, out var error);
        if (error is not null) return error;

        foreach (var device in devices)
        {
            var cfg = _config.Find(device.Uuid);
            if (cfg is not null) UpdateGpuConfig(cfg with { Override = null });

            device.RestoreDefaults();
            _applied.Remove(device.Uuid);
            _engine.Reset(device.Uuid);
            _log.Info($"{device.Info.Name} restored to defaults");
        }

        return new IpcResponse
        {
            Ok = true,
            Message = "restored default power limit and released clock locks",
            Gpus = BuildState(request.Gpu).Gpus,
        };
    }

    IpcResponse SetGpuConfig(IpcRequest request)
    {
        if (request.GpuConfig is null)
            return IpcResponse.Failure("set-gpu-config requires a gpuConfig body", 1);

        var device = _session.FindByUuid(request.GpuConfig.Uuid);
        if (device is null)
            return IpcResponse.Failure($"no GPU with UUID '{request.GpuConfig.Uuid}'", 3);

        UpdateGpuConfig(request.GpuConfig);
        _engine.Reset(device.Uuid);
        _applied.Remove(device.Uuid);   // force a re-apply under the new settings
        _log.Info($"configuration updated for {device.Info.Name}");

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = "configuration saved",
            Gpus = BuildState(device.Uuid).Gpus,
        };
    }

    // ---- helpers ---------------------------------------------------------------------

    static string Version =>
        typeof(GpuManager).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    IEnumerable<GpuDevice> ManagedDevices() =>
        _session.Devices.Where(d => _config.Find(d.Uuid)?.Managed == true);

    /// <summary>
    /// Resolves a selector to devices. An unmatched selector is an error rather than a silent
    /// no-op — a script that typos a UUID must not think it succeeded.
    /// </summary>
    List<GpuDevice> Select(string? selector, out IpcResponse? error)
    {
        var matched = _session.Devices.Where(d => GpuSelector.Matches(d.Info, selector)).ToList();
        error = matched.Count == 0
            ? IpcResponse.Failure($"no GPU matched '{selector}'", 3)
            : null;
        return matched;
    }

    void UpdateGpuConfig(GpuConfig updated)
    {
        lock (_gate)
        {
            _config = _config.WithGpu(updated);
            _store.Save(_config);
        }
    }

    public void Dispose() => _session.Dispose();
}
