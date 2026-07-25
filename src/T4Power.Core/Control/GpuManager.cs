using T4Power.Core.Fans;
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
    readonly FanManager _fans;
    readonly FileLog _log;
    readonly Func<DateTimeOffset> _clock;

    /// <summary>Guards the small pieces of shared state read by IPC responses.</summary>
    readonly object _gate = new();

    /// <summary>
    /// Serialises every operation that evaluates rules or writes to the GPU.
    ///
    /// <see cref="Tick"/> runs on the service's timer, but the IPC handlers call it directly too
    /// so a client sees its change immediately — so it genuinely runs on two threads. It mutates
    /// plain dictionaries (applied state, and the rule engine's hysteresis timestamps), and a
    /// Dictionary written concurrently can corrupt and spin forever. That is not theoretical:
    /// it wedged the service mid-session, freezing the last decision so the GPU stayed pinned
    /// at Max after the watched app had exited.
    ///
    /// Monitor is re-entrant, so a handler holding this can still call Tick.
    /// </summary>
    readonly object _tick = new();

    readonly Dictionary<string, AppliedState> _applied = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, GpuTelemetry> _lastTelemetry = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, RuleDecision> _lastDecision = new(StringComparer.OrdinalIgnoreCase);

    AppConfig _config;
    bool _disposed;

    public GpuManager(
        NvmlSession session,
        ConfigStore store,
        FileLog log,
        IProcessSource? processes = null,
        Func<DateTimeOffset>? clock = null,
        IFanHardware? fanHardware = null)
    {
        _session = session;
        _store = store;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _engine = new RuleEngine(_clock);
        _processes = processes ?? new ProcessWatcher(clock: _clock);

        // Fan control is strictly additive: with no hardware layer supplied it reports itself
        // unavailable and writes nothing, and everything else carries on exactly as before.
        _fans = new FanManager(fanHardware ?? new NullFanHardware(), log, _clock);

        // Discovery is by UUID, so a GPU that was moved to another slot keeps its settings and a
        // newly seen one picks up defaults.
        var discovered = _session.Devices.ToDictionary(
            d => d.Uuid, d => d.Info, StringComparer.OrdinalIgnoreCase);

        _config = _store.Load()
            .EnsureEntriesFor(discovered.Values)
            .Migrate(discovered, _log.Info);
        _store.Save(_config);

        _log.Info($"NVML {_session.DriverVersion}, {_session.Devices.Count} GPU(s): " +
                  string.Join(", ", _session.Devices.Select(d => $"{d.Info.Name} [{d.Info.Uuid}]")));
    }

    public AppConfig Config { get { lock (_gate) return _config; } }
    public string? DriverVersion => _session.DriverVersion;

    /// <summary>
    /// The fan layer. Reachable so the IPC handlers can enumerate and adopt headers, but every
    /// caller must hold <see cref="_tick"/> — <see cref="FanManager"/> deliberately takes no lock
    /// of its own.
    /// </summary>
    public FanManager Fans => _fans;

    // ---- startup / shutdown ----------------------------------------------------------

    /// <summary>
    /// Unwedges a GPU stuck at P0. A T4 can sit at P0/1590 MHz drawing 36 W with nothing
    /// running; a lock-then-unlock cycle drops it to P8/300 MHz and ~10 W. Measured on driver
    /// 610.74. Harmless on cards that idle correctly, so it just runs where it applies.
    /// </summary>
    public void KickStuckPStates()
    {
        lock (_tick) KickStuckPStatesCore();
    }

    void KickStuckPStatesCore()
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
        lock (_tick)
        {
            if (_disposed) return;

            // Two independent restores, so a failure in one still leaves the other done. An NVML
            // error must not be the reason a fan header stays pinned by a service that is exiting.
            try
            {
                foreach (var device in ManagedDevices())
                {
                    _log.Info($"restoring {device.Info.Name} to defaults " +
                              $"({device.Info.DefaultPowerLimitW:0.#} W, clocks unlocked)");
                    device.RestoreDefaults();
                    _applied.Remove(device.Uuid);
                }
            }
            catch (NvmlException ex)
            {
                _log.Error($"restoring GPU defaults failed: {ex.Message}");
            }

            // Last, so the headers are handed back reflecting the GPU's final state.
            _fans.ReleaseAll();
        }
    }

    // ---- the tick --------------------------------------------------------------------

    /// <summary>
    /// Reads telemetry, evaluates rules, and applies any change. Called once per poll, and also
    /// directly by IPC handlers so a client sees its change take effect immediately.
    /// </summary>
    public void Tick()
    {
        lock (_tick) TickCore();
    }

    void TickCore()
    {
        if (_disposed) return;   // shutdown began while this tick was queued

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

        // Fans last, so they steer on the telemetry this tick just read rather than the previous
        // one. Inside the same monitor as everything above, which is what FanManager assumes.
        AppConfig config;
        lock (_gate) config = _config;

        _fans.Apply(config, TelemetryFor, UpdateFanConfig);
    }

    GpuTelemetry? TelemetryFor(string uuid)
    {
        lock (_gate) return _lastTelemetry.GetValueOrDefault(uuid);
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
            // Held across the whole handler, not just its inner Tick: the mutating commands
            // read config, edit it and re-evaluate, and that sequence must not interleave with
            // the timer's own evaluation.
            lock (_tick) return Task.FromResult(Handle(request));
        }
        catch (NvmlException ex)
        {
            return Task.FromResult(IpcResponse.Failure(ex.Message,
                ex.IsPermissionDenied ? 5 : ex.IsNotSupported ? 6 : 8));
        }
        catch (FanHardwareException ex)
        {
            // Reported as "not supported" rather than a generic failure: from the caller's side a
            // header that cannot be written is the same class of problem as a GPU that will not
            // take a clock lock.
            return Task.FromResult(IpcResponse.Failure(ex.Message, 6));
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
        IpcCommands.AddWatch => EditWatchlist(request, add: true),
        IpcCommands.RemoveWatch => EditWatchlist(request, add: false),
        IpcCommands.ListFans => ListFans(),
        IpcCommands.AdoptFan => AdoptFan(request),
        IpcCommands.ReleaseFan => ReleaseFan(request),
        IpcCommands.SetFanConfig => SetFanConfig(request),
        IpcCommands.SetFanOverride => SetFanOverride(request),
        IpcCommands.ClearFanOverride => ClearFanOverride(request),
        _ => IpcResponse.Failure($"unknown command '{request.Command}'", 1),
    };

    // ---- fan commands ----------------------------------------------------------------

    /// <summary>
    /// Discovery. Unlike get-state, this reports every header on the board rather than only the
    /// adopted ones, each with a live reading — it is the command you use to work out which
    /// header is the one you care about, and it cannot do that job without the unadopted ones.
    /// </summary>
    IpcResponse ListFans() => new()
    {
        Ok = true,
        ServiceVersion = Version,
        FanChannels = _fans.Channels,
        Fans = NameSources(_fans.Survey()),
        FanHardware = FanHardwareInfo(),
    };

    /// <summary>Resolves each header's source GPU UUID to a readable name. Done here because the
    /// fan layer deliberately knows nothing about NVML.</summary>
    IReadOnlyList<FanStatus> NameSources(IReadOnlyList<FanStatus> statuses) => statuses
        .Select(s => s.SourceGpuUuid is { } uuid && _session.FindByUuid(uuid) is { } device
            ? s with { SourceGpuName = device.Info.Name }
            : s)
        .ToList();

    FanHardwareInfo FanHardwareInfo() => new()
    {
        Available = _fans.IsAvailable,
        Reason = _fans.UnavailableReason,
    };

    /// <summary>Rejects a fan request early when there is no hardware to act on, so the caller
    /// gets the PawnIO explanation rather than "no fan matched".</summary>
    IpcResponse? FanUnavailable() => _fans.IsAvailable
        ? null
        : IpcResponse.Failure(_fans.UnavailableReason ?? "fan control is not available", 6);

    /// <summary>
    /// Takes a header under management, driven by a named GPU's temperature.
    ///
    /// Deliberately verifies before committing: a selector that resolves to the wrong channel
    /// would otherwise silently take over the pump or a CPU fan, and the first symptom of that is
    /// thermal. A header that does not visibly respond is still adopted, but unverified and
    /// flagged, because "no tachometer" is a legitimate wiring choice and refusing outright would
    /// be worse than saying so.
    /// </summary>
    IpcResponse AdoptFan(IpcRequest request)
    {
        if (FanUnavailable() is { } unavailable) return unavailable;

        var channel = _fans.FindChannel(request.Fan);
        if (channel is null)
        {
            return IpcResponse.Failure(
                $"no fan header matched '{request.Fan}' (available: " +
                $"{string.Join(", ", _fans.Channels.Select(c => c.Identifier))})", 3);
        }

        var devices = Select(request.Gpu, out var error);
        if (error is not null) return error;
        if (devices.Count > 1)
        {
            return IpcResponse.Failure(
                "a fan header is driven by exactly one GPU; name which one with --gpu", 1);
        }

        var device = devices[0];
        var existing = _config.FindFan(channel.Identifier);

        var problem = _fans.Verify(channel, Thread.Sleep);

        var adopted = (existing ?? FanConfig.CreateDefault(channel.Identifier, device.Uuid)) with
        {
            ControlIdentifier = channel.Identifier,
            ChipName = channel.ChipName,
            ControlIndex = channel.Index,
            RpmSensorIdentifier = channel.RpmSensorIdentifier,
            FriendlyName = existing?.FriendlyName ?? channel.Name,
            SourceGpuUuid = device.Uuid,
            Managed = true,
            Verified = problem is null,
        };

        UpdateFanConfig(adopted);
        _fans.Reset(channel.Identifier);
        _log.Info($"adopted fan header {channel.Describe()}, driven by {device.Info.Name}" +
                  (problem is null ? "" : $" (unverified: {problem})"));

        Tick();

        var message = $"{channel.Describe()} is now driven by {device.Info.Name}'s temperature";
        return new IpcResponse
        {
            Ok = true,
            Message = problem is null ? message : $"{message}. Warning: {problem}",
            Fans = NameSources(_fans.Statuses),
            FanChannels = _fans.Channels,
            FanHardware = FanHardwareInfo(),
        };
    }

    /// <summary>
    /// Hands a header back to the BIOS and forgets it. Order matters: release first, because
    /// dropping the config would lose the identifier needed to do the releasing.
    /// </summary>
    IpcResponse ReleaseFan(IpcRequest request)
    {
        var fan = _config.Fans.FirstOrDefault(f => FanSelector.Matches(f, request.Fan));
        if (fan is null)
            return IpcResponse.Failure($"no managed fan header matched '{request.Fan}'", 3);

        if (_fans.FindChannel(fan.ControlIdentifier) is { } channel)
        {
            try
            {
                _fans.Release(channel);
            }
            catch (FanHardwareException ex)
            {
                _log.Error($"releasing {channel.Describe()} failed: {ex.Message}");
            }
        }

        lock (_gate)
        {
            _config = _config.WithoutFan(fan.ControlIdentifier);
            _store.Save(_config);
        }

        _fans.Reset(fan.ControlIdentifier);
        _log.Info($"released fan header {fan.DisplayName}; it is back on the BIOS curve");

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = $"{fan.DisplayName} is back under BIOS control",
            Fans = NameSources(_fans.Statuses),
            FanChannels = _fans.Channels,
            FanHardware = FanHardwareInfo(),
        };
    }

    /// <summary>Replaces one header's settings, used by the curve editor.</summary>
    IpcResponse SetFanConfig(IpcRequest request)
    {
        if (request.FanConfig is null)
            return IpcResponse.Failure("set-fan-config requires a fanConfig body", 1);

        var incoming = request.FanConfig;
        var existing = _config.FindFan(incoming.ControlIdentifier);
        if (existing is null)
        {
            return IpcResponse.Failure(
                $"fan header '{incoming.ControlIdentifier}' is not managed; adopt it first", 3);
        }

        if (_session.FindByUuid(incoming.SourceGpuUuid) is null)
            return IpcResponse.Failure($"no GPU with UUID '{incoming.SourceGpuUuid}'", 3);

        // Points arrive from a client that may have dragged them into any order and off the axis,
        // so normalise before anything evaluates them.
        var updated = incoming with
        {
            Curve = incoming.Curve.Normalised(),
            Verified = existing.Verified,
        };

        UpdateFanConfig(updated);
        _fans.Reset(updated.ControlIdentifier);
        _log.Info($"fan configuration updated for {updated.DisplayName}");

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = "fan configuration saved",
            Fans = NameSources(_fans.Statuses),
            FanHardware = FanHardwareInfo(),
        };
    }

    /// <summary>
    /// Holds a header at a fixed duty. Also the "which fan is this?" gesture — a short TTL and
    /// 100% spins one up audibly and then puts it back on its own.
    /// </summary>
    IpcResponse SetFanOverride(IpcRequest request)
    {
        if (request.FanPercent is not { } percent)
            return IpcResponse.Failure("set-fan-override requires a fanPercent", 1);

        if (percent is < 0 or > 100)
            return IpcResponse.Failure($"{percent:0.#}% is outside the 0-100 range", 4);

        var fan = _config.Fans.FirstOrDefault(f => FanSelector.Matches(f, request.Fan));
        if (fan is null)
            return IpcResponse.Failure($"no managed fan header matched '{request.Fan}'", 3);

        var expires = request.DurationSeconds is { } seconds and > 0
            ? _clock().AddSeconds(seconds)
            : (DateTimeOffset?)null;

        UpdateFanConfig(fan with { Override = new FanOverride { Percent = percent, ExpiresUtc = expires } });

        var ttl = expires is null ? "until cleared" : $"for {request.DurationSeconds}s";
        _log.Info($"fan override set: {fan.DisplayName} -> {percent:0.#}% {ttl}");

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = $"{fan.DisplayName} held at {percent:0.#}% {ttl}",
            Fans = NameSources(_fans.Statuses),
            FanHardware = FanHardwareInfo(),
        };
    }

    IpcResponse ClearFanOverride(IpcRequest request)
    {
        var fan = _config.Fans.FirstOrDefault(f => FanSelector.Matches(f, request.Fan));
        if (fan is null)
            return IpcResponse.Failure($"no managed fan header matched '{request.Fan}'", 3);

        if (fan.Override is not null)
        {
            UpdateFanConfig(fan with { Override = null });
            _log.Info($"fan override cleared: {fan.DisplayName} back to its curve");
        }

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = $"{fan.DisplayName} is back on its curve",
            Fans = NameSources(_fans.Statuses),
            FanHardware = FanHardwareInfo(),
        };
    }

    /// <summary>
    /// Adds or removes executable names on the process-name rule. Done service-side rather than
    /// by round-tripping a whole config through the client, so a stale or malformed client
    /// cannot clobber profiles it never meant to touch.
    /// </summary>
    IpcResponse EditWatchlist(IpcRequest request, bool add)
    {
        if (request.Match.Count == 0)
            return IpcResponse.Failure("no executable names given", 1);

        var devices = Select(request.Gpu, out var error);
        if (error is not null) return error;

        var names = request.Match
            .Select(ProcessWatcher.Normalise)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var notes = new List<string>();

        foreach (var device in devices)
        {
            var cfg = _config.Find(device.Uuid);
            if (cfg is null) continue;

            var target = request.Profile ?? Profile.Max;
            if (add && cfg.FindProfile(target) is null)
            {
                return IpcResponse.Failure(
                    $"{device.Info.Name} has no profile '{target}' " +
                    $"(available: {string.Join(", ", cfg.Profiles.Select(p => p.Name))})", 1);
            }

            var rules = cfg.Rules.ToList();
            var index = rules.FindIndex(r => r.Type == RuleType.ProcessName &&
                                             string.Equals(r.ProfileName, target, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                if (!add)
                {
                    notes.Add($"{device.Info.Name}: no watchlist for '{target}'");
                    continue;
                }

                rules.Insert(0, new Rule
                {
                    Type = RuleType.ProcessName,
                    Priority = 100,
                    ProfileName = target,
                    Match = names,
                    Enabled = true,
                    DwellSeconds = 60,
                });
                notes.Add($"{device.Info.Name}: watching {string.Join(", ", names)} -> {target}");
            }
            else
            {
                var existing = rules[index];
                var merged = add
                    ? existing.Match.Union(names, StringComparer.OrdinalIgnoreCase).ToList()
                    : existing.Match.Except(names, StringComparer.OrdinalIgnoreCase).ToList();

                rules[index] = existing with
                {
                    Match = merged,
                    // A rule matching nothing would fire on every tick if left enabled.
                    Enabled = merged.Count > 0,
                };

                notes.Add(merged.Count > 0
                    ? $"{device.Info.Name}: watching {string.Join(", ", merged)} -> {target}"
                    : $"{device.Info.Name}: watchlist for {target} is now empty (rule disabled)");
            }

            UpdateGpuConfig(cfg with { Rules = rules });
            _engine.Reset(device.Uuid);
        }

        Tick();
        return new IpcResponse
        {
            Ok = true,
            Message = string.Join("; ", notes),
            Gpus = BuildState(request.Gpu).Gpus,
        };
    }

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
                Fans = NameSources(_fans.Statuses),

                // Included in the ordinary poll, not just list-fans: it is a handful of small
                // records, and it means the UI's adoption picker stays live - a header that
                // appears once PawnIO starts shows up without anyone hitting refresh.
                FanChannels = _fans.Channels,
                FanHardware = FanHardwareInfo(),
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

    void UpdateFanConfig(FanConfig updated)
    {
        lock (_gate)
        {
            _config = _config.WithFan(updated);
            _store.Save(_config);
        }
    }

    /// <summary>
    /// Shuts down NVML. Takes the same lock as <see cref="Tick"/> so an in-flight evaluation
    /// cannot be left calling into a library that has already been torn down — that race is
    /// what produced "Uninitialized" errors during service shutdown.
    /// </summary>
    public void Dispose()
    {
        lock (_tick)
        {
            if (_disposed) return;
            _disposed = true;
            _fans.Dispose();
            _session.Dispose();
        }
    }
}
