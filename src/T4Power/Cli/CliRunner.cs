using System.Text.Json;
using T4Power.Core.Fans;
using T4Power.Core.Ipc;
using T4Power.Core.Model;
using T4Power.Core.Nvml;
using T4Power.Core.Rules;

namespace T4Power.Cli;

/// <summary>
/// The CLI. Read verbs go straight to NVML — reads need no elevation, so <c>--list</c> and
/// <c>--status</c> work even with the service stopped. Write verbs go through the service's
/// pipe, which is what lets an ordinary user (or an agent running as one) change GPU state
/// without an admin token.
/// </summary>
internal static class CliRunner
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(CommandLineOptions o)
    {
        if (o.Error is not null)
        {
            Fail(o, o.Error);
            return ExitCode.UsageError;
        }

        return o.Verb switch
        {
            Verb.List => List(o),
            Verb.Status => await StatusAsync(o).ConfigureAwait(false),
            Verb.Set => await SetAsync(o).ConfigureAwait(false),
            Verb.Profile => await SetAsync(o).ConfigureAwait(false),
            Verb.Auto => await SendAsync(o, new IpcRequest { Command = IpcCommands.ClearOverride, Gpu = o.GpuSelector })
                .ConfigureAwait(false),
            Verb.RestoreDefaults => await SendAsync(o, new IpcRequest { Command = IpcCommands.RestoreDefaults, Gpu = o.GpuSelector })
                .ConfigureAwait(false),

            Verb.Watch => await SendAsync(o, new IpcRequest
            {
                Command = IpcCommands.AddWatch,
                Gpu = o.GpuSelector,
                Profile = o.ProfileName,
                Match = o.Match,
            }).ConfigureAwait(false),

            Verb.Unwatch => await SendAsync(o, new IpcRequest
            {
                Command = IpcCommands.RemoveWatch,
                Gpu = o.GpuSelector,
                Profile = o.ProfileName,
                Match = o.Match,
            }).ConfigureAwait(false),

            Verb.Rules => await RulesAsync(o).ConfigureAwait(false),

            // ---- fan control ----------------------------------------------------------

            Verb.Fans => await FansAsync(o).ConfigureAwait(false),

            Verb.AdoptFan => await SendAsync(o, new IpcRequest
            {
                Command = IpcCommands.AdoptFan,
                Fan = o.FanSelector,
                Gpu = o.GpuSelector,
            }).ConfigureAwait(false),

            Verb.ReleaseFan => await SendAsync(o, new IpcRequest
            {
                Command = IpcCommands.ReleaseFan,
                Fan = o.FanSelector,
            }).ConfigureAwait(false),

            Verb.FanSet => await FanSetAsync(o).ConfigureAwait(false),

            Verb.FanAuto => await SendAsync(o, new IpcRequest
            {
                Command = IpcCommands.ClearFanOverride,
                Fan = o.FanSelector,
            }).ConfigureAwait(false),

            // "Which fan is this?" is just a loud override with a short leash. Defaults chosen so
            // the bare verb is safe: full speed, and back on its own after ten seconds even if
            // this process is killed the moment after sending it.
            Verb.IdentifyFan => await SendAsync(o, new IpcRequest
            {
                Command = IpcCommands.SetFanOverride,
                Fan = o.FanSelector,
                FanPercent = o.Percent ?? 100,
                DurationSeconds = (int)(o.Duration?.TotalSeconds ?? 10),
            }).ConfigureAwait(false),

            Verb.FanCurve => await FanCurveAsync(o).ConfigureAwait(false),

            _ => Usage(o),
        };
    }

    // ---- fan verbs --------------------------------------------------------------------

    /// <summary>
    /// Every writable header on the board, adopted or not. This is the discovery step: it is how
    /// you find out which index the fan you care about is on, before adopting anything.
    /// </summary>
    static async Task<int> FansAsync(CommandLineOptions o)
    {
        try
        {
            var response = await new IpcClient()
                .SendAsync(new IpcRequest { Command = IpcCommands.ListFans })
                .ConfigureAwait(false);

            if (!response.Ok)
            {
                Fail(o, response.Error ?? "the service reported a failure");
                return response.Code;
            }

            if (o.Json)
            {
                var channels = response.FanChannels.ToDictionary(
                    c => c.Identifier, StringComparer.OrdinalIgnoreCase);

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    available = response.FanHardware?.Available ?? false,
                    reason = response.FanHardware?.Reason,
                    headers = response.Fans.Select(f => new
                    {
                        identifier = f.ControlIdentifier,
                        name = f.Name,
                        chip = f.ChipName,
                        index = f.Index,

                        // Whether a header has a paired tachometer decides whether adoption can
                        // verify it, so it belongs in the machine-readable output.
                        rpmSensor = channels.GetValueOrDefault(f.ControlIdentifier)?.RpmSensorIdentifier,
                        managed = f.Managed,
                        verified = f.Verified,
                        present = f.Present,
                        readbackPercent = f.ReadbackPercent,
                        rpm = f.Rpm,
                        mode = f.Mode.ToString(),
                        sourceGpuUuid = f.SourceGpuUuid,
                        sourceTemperatureC = f.SourceTemperatureC,
                        commandedPercent = f.CommandedPercent,
                        source = f.Source.ToString(),
                        reason = f.Reason,
                    }),
                }, Json));
                return ExitCode.Ok;
            }

            if (response.FanHardware is { Available: false })
            {
                Console.WriteLine($"Fan control is unavailable: {response.FanHardware.Reason}");
                return ExitCode.NotSupported;
            }

            if (response.Fans.Count == 0)
            {
                Console.WriteLine("No controllable fan headers were found.");
                return ExitCode.Ok;
            }

            foreach (var chip in response.Fans.GroupBy(f => f.ChipName ?? "unknown chip"))
            {
                Console.WriteLine($"Fan headers on {chip.Key}");
                Console.WriteLine();

                foreach (var f in chip.OrderBy(f => f.Index))
                {
                    // A channel nobody has written to reports Undefined, which is a library
                    // detail, not a state a user cares about — it means the BIOS still owns it.
                    var owner = f.Mode == FanControlMode.Software ? "T4Power" : "BIOS";

                    Console.WriteLine(
                        $"  [{f.Index}] {f.Name,-9} {(f.ReadbackPercent is { } d ? $"{d,3:0}%" : "   -")} " +
                        $"{(f.Rpm is { } rpm ? $"{rpm,5} RPM" : "    - RPM")}   {owner}");

                    Console.WriteLine($"       {f.ControlIdentifier}");

                    if (f.Managed)
                    {
                        Console.WriteLine($"       driven by {f.SourceGpuName ?? f.SourceGpuUuid}" +
                                          (f.Verified ? "" : "   (unverified — RPM response never confirmed)"));
                        Console.WriteLine($"       {f.Reason}");
                    }

                    Console.WriteLine();
                }
            }

            Console.WriteLine("The last column is who owns each header right now.");
            Console.WriteLine("Use --identify-fan <sel> to spin one up, then --adopt-fan <sel> --gpu <sel>.");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is IpcClient.UnavailableException or IpcClient.AccessDeniedException)
        {
            // No NVML fallback here, unlike --status: reaching the SuperIO needs the driver and
            // elevation, so without the service there is genuinely nothing to read.
            Fail(o, ex.Message);
            return ExitCode.ServiceUnavailable;
        }
    }

    static Task<int> FanSetAsync(CommandLineOptions o)
    {
        if (o.Percent is null)
        {
            Fail(o, "nothing to set - give --percent");
            return Task.FromResult(ExitCode.UsageError);
        }

        return SendAsync(o, new IpcRequest
        {
            Command = IpcCommands.SetFanOverride,
            Fan = o.FanSelector,
            FanPercent = o.Percent,
            DurationSeconds = o.Duration is { } d ? (int)d.TotalSeconds : null,
        });
    }

    /// <summary>
    /// Shows a header's curve, or replaces its points. The headless way to configure fan control,
    /// which is what lets the cutover from another fan app happen without waiting on the UI.
    /// </summary>
    static async Task<int> FanCurveAsync(CommandLineOptions o)
    {
        // Round-trip the stored config rather than inventing one, so the hysteresis settings, the
        // panic temperature and the source GPU all survive a curve edit. Same reasoning as the
        // UI's "manage this GPU" path, which reads the store for the same reason.
        var stored = new ConfigStore().Load();
        var fan = stored.Fans.FirstOrDefault(f => FanSelector.Matches(f, o.FanSelector));

        if (fan is null)
        {
            Fail(o, $"no managed fan header matched '{o.FanSelector}' - adopt one first with --adopt-fan");
            return ExitCode.UnknownGpu;
        }

        if (o.CurvePoints is null)
        {
            if (o.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    identifier = fan.ControlIdentifier,
                    name = fan.DisplayName,
                    sourceGpuUuid = fan.SourceGpuUuid,
                    minTemperatureC = fan.Curve.MinTemperatureC,
                    maxTemperatureC = fan.Curve.MaxTemperatureC,
                    minPercent = fan.Curve.MinPercent,
                    maxPercent = fan.Curve.MaxPercent,
                    panicTemperatureC = fan.PanicTemperatureC,
                    points = fan.Curve.Points.Select(p => new { p.TemperatureC, p.Percent }),
                    hysteresis = fan.Curve.Hysteresis,
                }, Json));
                return ExitCode.Ok;
            }

            Console.WriteLine($"{fan.DisplayName}  [{fan.ControlIdentifier}]");
            Console.WriteLine($"  driven by      {fan.SourceGpuUuid}");
            Console.WriteLine($"  curve          {string.Join(", ", fan.Curve.Points.Select(p => $"{p.TemperatureC:0.#}C:{p.Percent:0.#}%"))}");
            Console.WriteLine($"  duty bounds    {fan.Curve.MinPercent:0.#}-{fan.Curve.MaxPercent:0.#}%");
            Console.WriteLine($"  temp axis      {fan.Curve.MinTemperatureC:0.#}-{fan.Curve.MaxTemperatureC:0.#} C");
            Console.WriteLine($"  response       up {fan.Curve.Hysteresis.ResponseTimeUpSeconds:0.#}s, down {fan.Curve.Hysteresis.ResponseTimeDownSeconds:0.#}s");
            Console.WriteLine($"  hysteresis     +{fan.Curve.Hysteresis.HysteresisUpC:0.#} / -{fan.Curve.Hysteresis.HysteresisDownC:0.#} C");
            Console.WriteLine($"  panic          full speed at or above {fan.PanicTemperatureC:0.#} C");
            return ExitCode.Ok;
        }

        return await SendAsync(o, new IpcRequest
        {
            Command = IpcCommands.SetFanConfig,
            FanConfig = fan with { Curve = fan.Curve with { Points = o.CurvePoints } },
        }).ConfigureAwait(false);
    }

    /// <summary>Shows the auto-switch rules in priority order, plus the profiles they target.</summary>
    static async Task<int> RulesAsync(CommandLineOptions o)
    {
        try
        {
            var response = await new IpcClient()
                .SendAsync(new IpcRequest { Command = IpcCommands.GetState, Gpu = o.GpuSelector })
                .ConfigureAwait(false);

            if (!response.Ok)
            {
                Fail(o, response.Error ?? "the service reported a failure");
                return response.Code;
            }

            if (o.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(response.Gpus.Select(g => new
                {
                    uuid = g.Uuid,
                    name = g.Name,
                    managed = g.Managed,
                    thermalGuardC = g.ThermalGuardC,
                    profiles = g.Profiles.Select(p => new
                    {
                        name = p.Name,
                        powerLimitW = p.PowerLimitW,
                        lockClocks = p.LockClocks is null ? null : new { p.LockClocks.MinMhz, p.LockClocks.MaxMhz },
                    }),
                    rules = g.Rules.Select(r => new
                    {
                        type = r.Type.ToString(),
                        profile = r.ProfileName,
                        r.Priority,
                        r.Enabled,
                        r.Match,
                        r.UtilThreshold,
                        r.DwellSeconds,
                    }),
                }), Json));
                return ExitCode.Ok;
            }

            foreach (var g in response.Gpus)
            {
                Console.WriteLine($"{g.Name}  [{g.Uuid}]{(g.Managed ? "" : "   (not managed)")}");

                Console.WriteLine("  profiles:");
                foreach (var p in g.Profiles)
                    Console.WriteLine($"    {p.Name,-10} {p.Describe()}");

                Console.WriteLine("  rules (highest priority first):");
                foreach (var r in g.Rules.OrderByDescending(r => r.Priority))
                    Console.WriteLine($"    [{r.Priority,3}] {(r.Enabled ? " " : "off")} {r.Describe()}");

                if (g.ThermalGuardC is { } guard)
                    Console.WriteLine($"  thermal guard: force Eco at or above {guard} C");
                Console.WriteLine();
            }
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is IpcClient.UnavailableException or IpcClient.AccessDeniedException)
        {
            Fail(o, ex.Message);
            return ExitCode.ServiceUnavailable;
        }
    }

    // ---- read verbs: direct NVML, no service needed -----------------------------------

    static int List(CommandLineOptions o) => WithNvml(o, session =>
    {
        var gpus = session.Devices.Where(d => GpuSelector.Matches(d.Info, o.GpuSelector)).ToList();
        if (gpus.Count == 0) return NoMatch(o);

        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                driverVersion = session.DriverVersion,
                gpus = gpus.Select(d => new
                {
                    index = d.Info.Index,
                    uuid = d.Info.Uuid,
                    name = d.Info.Name,
                    serial = d.Info.Serial,
                    pciBusId = d.Info.PciBusId,
                    isTeslaT4 = d.Info.IsTeslaT4,
                    power = new
                    {
                        supported = d.Info.SupportsPowerLimit,
                        minW = d.Info.MinPowerLimitW,
                        maxW = d.Info.MaxPowerLimitW,
                        defaultW = d.Info.DefaultPowerLimitW,
                        currentW = d.GetPowerLimitW(),
                    },
                    clocks = new
                    {
                        minMhz = d.Info.MinGraphicsClockMhz,
                        maxMhz = d.Info.MaxGraphicsClockMhz,
                        steps = d.Info.SupportedGraphicsClocksMhz.Count,
                    },
                }),
            }, Json));
            return ExitCode.Ok;
        }

        Console.WriteLine($"Driver {session.DriverVersion ?? "unknown"} - {gpus.Count} GPU(s)");
        foreach (var d in gpus)
        {
            var i = d.Info;
            Console.WriteLine();
            Console.WriteLine($"  [{i.Index}] {i.Name}{(i.IsTeslaT4 ? "  (T4)" : "")}");
            Console.WriteLine($"       uuid    {i.Uuid}");
            if (i.PciBusId is not null) Console.WriteLine($"       pci     {i.PciBusId}");
            if (i.Serial is not null) Console.WriteLine($"       serial  {i.Serial}");
            Console.WriteLine(i.SupportsPowerLimit
                ? $"       power   {d.GetPowerLimitW():0.#} W now - range {i.MinPowerLimitW:0.#}-{i.MaxPowerLimitW:0.#} W, default {i.DefaultPowerLimitW:0.#} W"
                : "       power   not manageable on this GPU");
            Console.WriteLine(i.SupportedGraphicsClocksMhz.Count > 0
                ? $"       clocks  {i.MinGraphicsClockMhz}-{i.MaxGraphicsClockMhz} MHz ({i.SupportedGraphicsClocksMhz.Count} steps)"
                : "       clocks  no supported-clock list reported");
        }
        return ExitCode.Ok;
    });

    /// <summary>
    /// Prefers the service, because only it knows the active profile and why. Falls back to a
    /// direct NVML read when the service is down, so status is never simply unavailable.
    /// </summary>
    static async Task<int> StatusAsync(CommandLineOptions o)
    {
        try
        {
            var response = await new IpcClient()
                .SendAsync(new IpcRequest { Command = IpcCommands.GetState, Gpu = o.GpuSelector })
                .ConfigureAwait(false);

            if (!response.Ok)
            {
                Fail(o, response.Error ?? "the service reported a failure");
                return response.Code;
            }

            PrintServiceStatus(o, response);
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is IpcClient.UnavailableException or IpcClient.AccessDeniedException)
        {
            if (!o.Json)
                Console.Error.WriteLine($"note: {ex.Message}; reading the GPU directly instead.");
            return StatusDirect(o);
        }
    }

    static void PrintServiceStatus(CommandLineOptions o, IpcResponse response)
    {
        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "service",
                serviceVersion = response.ServiceVersion,
                driverVersion = response.DriverVersion,
                gpus = response.Gpus.Select(g => new
                {
                    uuid = g.Uuid,
                    name = g.Name,
                    managed = g.Managed,
                    activeProfile = g.ActiveProfile,
                    reason = g.Reason,
                    source = g.Source.ToString(),
                    overrideExpiresUtc = g.Override?.ExpiresUtc,
                    powerDrawW = g.Telemetry?.PowerDrawW,
                    powerLimitW = g.Telemetry?.PowerLimitW,
                    minPowerLimitW = g.MinPowerLimitW,
                    maxPowerLimitW = g.MaxPowerLimitW,
                    smClockMhz = g.Telemetry?.SmClockMhz,
                    temperatureC = g.Telemetry?.TemperatureC,
                    gpuUtilPercent = g.Telemetry?.GpuUtilPercent,
                    pState = g.Telemetry?.PState?.ToString(),
                    activePids = g.Telemetry?.ActivePids ?? [],
                    throttleReasons = g.Telemetry?.InterestingReasons() ?? [],
                }),
            }, Json));
            return;
        }

        foreach (var g in response.Gpus)
        {
            var t = g.Telemetry;
            Console.WriteLine($"{g.Name}  [{g.Uuid}]");
            Console.WriteLine(
                $"  {t?.PowerDrawW:0.0} W / {t?.PowerLimitW:0.#} W cap   {t?.SmClockMhz} MHz   " +
                $"{t?.TemperatureC} C   {t?.GpuUtilPercent}% util   {t?.PState}");

            Console.WriteLine(g.Managed
                ? $"  profile: {g.ActiveProfile ?? "none"}   ({g.Reason})"
                : "  not managed by T4Power");

            if (t?.ActivePids.Count > 0)
                Console.WriteLine($"  active pids: {string.Join(", ", t.ActivePids)}");

            var reasons = string.Join(", ", t?.InterestingReasons() ?? []);
            if (reasons.Length > 0) Console.WriteLine($"  throttle: {reasons}");
            Console.WriteLine();
        }

        foreach (var fan in response.Fans)
        {
            Console.WriteLine($"{fan.Name ?? fan.ControlIdentifier}  (fan)");
            Console.WriteLine(
                $"  {fan.ReadbackPercent:0.#}% duty" +
                $"{(fan.Rpm is { } rpm ? $"   {rpm} RPM" : "")}" +
                $"{(fan.SourceTemperatureC is { } temp ? $"   source {temp} C" : "")}");
            Console.WriteLine($"  {fan.Reason}");
            if (!fan.Present) Console.WriteLine("  this header is not currently present on the board");
            Console.WriteLine();
        }
    }

    static int StatusDirect(CommandLineOptions o) => WithNvml(o, session =>
    {
        var gpus = session.Devices.Where(d => GpuSelector.Matches(d.Info, o.GpuSelector)).ToList();
        if (gpus.Count == 0) return NoMatch(o);

        var readings = gpus.Select(d => (d.Info, Telemetry: d.ReadTelemetry())).ToList();

        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "nvml",   // the service was unreachable, so there is no profile info
                gpus = readings.Select(r => new
                {
                    uuid = r.Info.Uuid,
                    name = r.Info.Name,
                    powerDrawW = r.Telemetry.PowerDrawW,
                    powerLimitW = r.Telemetry.PowerLimitW,
                    minPowerLimitW = r.Info.MinPowerLimitW,
                    maxPowerLimitW = r.Info.MaxPowerLimitW,
                    smClockMhz = r.Telemetry.SmClockMhz,
                    temperatureC = r.Telemetry.TemperatureC,
                    gpuUtilPercent = r.Telemetry.GpuUtilPercent,
                    pState = r.Telemetry.PState?.ToString(),
                    activePids = r.Telemetry.ActivePids,
                    throttleReasons = r.Telemetry.InterestingReasons(),
                }),
            }, Json));
            return ExitCode.Ok;
        }

        foreach (var (info, t) in readings)
        {
            Console.WriteLine($"{info.Name}  [{info.ShortId}]");
            Console.WriteLine(
                $"  {t.PowerDrawW:0.0} W / {t.PowerLimitW:0.#} W cap   {t.SmClockMhz} MHz   " +
                $"{t.TemperatureC} C   {t.GpuUtilPercent}% util   {t.PState}");
            if (t.ActivePids.Count > 0)
                Console.WriteLine($"  active pids: {string.Join(", ", t.ActivePids)}");
            var reasons = string.Join(", ", t.InterestingReasons());
            if (reasons.Length > 0) Console.WriteLine($"  throttle: {reasons}");
        }
        return ExitCode.Ok;
    });

    // ---- write verbs: through the service ---------------------------------------------

    static Task<int> SetAsync(CommandLineOptions o)
    {
        if (o.ProfileName is null && o.PowerW is null && o.Clocks is null)
        {
            Fail(o, "nothing to set - give --profile, --power and/or --clocks");
            return Task.FromResult(ExitCode.UsageError);
        }

        var request = new IpcRequest
        {
            Command = IpcCommands.SetOverride,
            Gpu = o.GpuSelector,
            Profile = o.ProfileName,
            PowerLimitW = o.PowerW,
            LockClocks = o.Clocks is { Unlock: false } c
                ? new ClockLock { MinMhz = c.MinMhz, MaxMhz = c.MaxMhz }
                : null,
            UnlockClocks = o.Clocks?.Unlock ?? false,
            DurationSeconds = o.Duration is { } d ? (int)d.TotalSeconds : null,
        };

        return SendAsync(o, request);
    }

    static async Task<int> SendAsync(CommandLineOptions o, IpcRequest request)
    {
        try
        {
            var response = await new IpcClient().SendAsync(request).ConfigureAwait(false);

            if (!response.Ok)
            {
                Fail(o, response.Error ?? "the service reported a failure");
                return response.Code == 0 ? ExitCode.Failed : response.Code;
            }

            if (o.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = response.Message,
                    gpus = response.Gpus.Select(g => new
                    {
                        uuid = g.Uuid,
                        name = g.Name,
                        activeProfile = g.ActiveProfile,
                        reason = g.Reason,
                        powerLimitW = g.Telemetry?.PowerLimitW,
                        smClockMhz = g.Telemetry?.SmClockMhz,
                        overrideExpiresUtc = g.Override?.ExpiresUtc,
                    }),
                }, Json));
            }
            else if (!string.IsNullOrWhiteSpace(response.Message))
            {
                Console.WriteLine(response.Message);
            }

            return ExitCode.Ok;
        }
        catch (IpcClient.UnavailableException ex)
        {
            Fail(o, ex.Message);
            return ExitCode.ServiceUnavailable;
        }
        catch (IpcClient.AccessDeniedException ex)
        {
            Fail(o, $"{ex.Message}. Re-run --install-service from this account to grant it access.");
            return ExitCode.PermissionDenied;
        }
        catch (IOException ex)
        {
            Fail(o, $"lost the connection to the service: {ex.Message}");
            return ExitCode.ServiceUnavailable;
        }
    }

    // ---- plumbing --------------------------------------------------------------------

    static int WithNvml(CommandLineOptions o, Func<NvmlSession, int> body)
    {
        try
        {
            using var session = NvmlSession.Open();
            return body(session);
        }
        catch (NvmlException ex)
        {
            Fail(o, ex.Message);
            return ex.IsPermissionDenied ? ExitCode.PermissionDenied : ExitCode.NvmlUnavailable;
        }
        catch (DllNotFoundException)
        {
            Fail(o, "nvml.dll not found - is an NVIDIA driver installed?");
            return ExitCode.NvmlUnavailable;
        }
    }

    static int NoMatch(CommandLineOptions o)
    {
        Fail(o, $"no GPU matched '{o.GpuSelector}'");
        return ExitCode.UnknownGpu;
    }

    static int Usage(CommandLineOptions o)
    {
        Fail(o, "no command given");
        CommandLine.PrintUsage();
        return ExitCode.UsageError;
    }

    /// <summary>Errors go to stderr, as JSON when --json is set so failures stay parseable.</summary>
    static void Fail(CommandLineOptions o, string message) =>
        Console.Error.WriteLine(o.Json
            ? JsonSerializer.Serialize(new { ok = false, error = message }, Json)
            : $"error: {message}");
}
