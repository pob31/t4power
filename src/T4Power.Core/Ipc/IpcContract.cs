using System.Text.Json;
using System.Text.Json.Serialization;
using T4Power.Core.Fans;
using T4Power.Core.Model;
using T4Power.Core.Rules;

namespace T4Power.Core.Ipc;

/// <summary>
/// Wire format for the control pipe.
///
/// The protocol is newline-delimited JSON, so <see cref="JsonSerializerOptions.WriteIndented"/>
/// MUST stay false — indented output embeds newlines and every message would be truncated at
/// its first line. This is why the IPC options are separate from
/// <see cref="Model.ConfigStore.JsonOptions"/>, which is indented for human editing.
/// </summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetState = "get-state";
    public const string GetConfig = "get-config";
    public const string SetOverride = "set-override";
    public const string ClearOverride = "clear-override";
    public const string RestoreDefaults = "restore-defaults";
    public const string SetGpuConfig = "set-gpu-config";
    public const string AddWatch = "add-watch";
    public const string RemoveWatch = "remove-watch";

    // Fan control. There is deliberately no "identify this header" command: spinning a fan up so
    // you can hear which one it is *is* an override with a short TTL, and giving it its own verb
    // would mean a second path that reaches the chip with its own validation to get wrong.
    public const string ListFans = "list-fans";
    public const string AdoptFan = "adopt-fan";
    public const string ReleaseFan = "release-fan";
    public const string SetFanConfig = "set-fan-config";
    public const string SetFanOverride = "set-fan-override";
    public const string ClearFanOverride = "clear-fan-override";
}

/// <summary>
/// A request from an unelevated client. Everything here is untrusted input: the pipe is
/// reachable by any principal on its ACL, so the service validates and clamps every field
/// against live NVML constraints rather than acting on it directly.
/// </summary>
public sealed record IpcRequest
{
    public required string Command { get; init; }

    /// <summary>UUID, index, or name substring. Null means every managed GPU.</summary>
    public string? Gpu { get; init; }

    public string? Profile { get; init; }
    public double? PowerLimitW { get; init; }
    public ClockLock? LockClocks { get; init; }
    public bool UnlockClocks { get; init; }

    /// <summary>TTL for an override. Null means it holds until cleared.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>Replacement config for one GPU, used by the rule editor.</summary>
    public GpuConfig? GpuConfig { get; init; }

    /// <summary>Executable names for the watchlist commands.</summary>
    public IReadOnlyList<string> Match { get; init; } = [];

    // ---- fan control -----------------------------------------------------------------

    /// <summary>Fan selector: full control identifier, its trailing segment, an index, or a name
    /// substring. Unlike <see cref="Gpu"/>, null matches nothing rather than everything.</summary>
    public string? Fan { get; init; }

    /// <summary>Duty for a manual fan override, 0-100. Clamped service-side to what the chip
    /// accepts before it reaches any hardware.</summary>
    public double? FanPercent { get; init; }

    /// <summary>Replacement settings for one header, used by the curve editor.</summary>
    public FanConfig? FanConfig { get; init; }
}

/// <summary>Whether fan control is working at all, and what to do about it if not.</summary>
public sealed record FanHardwareInfo
{
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

/// <summary>One GPU as the service sees it: identity, live telemetry, and why it is where it is.</summary>
public sealed record GpuStateDto
{
    public required string Uuid { get; init; }
    public required string Name { get; init; }
    public int Index { get; init; }
    public string? PciBusId { get; init; }
    public bool Managed { get; init; }

    public double MinPowerLimitW { get; init; }
    public double MaxPowerLimitW { get; init; }
    public double DefaultPowerLimitW { get; init; }
    public bool SupportsPowerLimit { get; init; }
    public uint MinGraphicsClockMhz { get; init; }
    public uint MaxGraphicsClockMhz { get; init; }

    public GpuTelemetry? Telemetry { get; init; }

    /// <summary>Profile currently applied, if any.</summary>
    public string? ActiveProfile { get; init; }

    /// <summary>Plain-language explanation, e.g. "manual override -> Max, expires in 28m".</summary>
    public string? Reason { get; init; }

    public DecisionSource Source { get; init; }
    public Override? Override { get; init; }

    /// <summary>Null until a clock-lock write has been tried; false if the GPU refused it.</summary>
    public bool? LockedClocksSupported { get; init; }

    public IReadOnlyList<Profile> Profiles { get; init; } = [];
    public IReadOnlyList<Rule> Rules { get; init; } = [];
    public uint? ThermalGuardC { get; init; }
}

public sealed record IpcResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    /// <summary>Maps to the CLI's process exit code so failures are distinguishable by scripts.</summary>
    public int Code { get; init; }

    public string? ServiceVersion { get; init; }
    public string? DriverVersion { get; init; }
    public IReadOnlyList<GpuStateDto> Gpus { get; init; } = [];

    /// <summary>
    /// Every fan header T4Power is driving, with live state and the reason for it.
    ///
    /// <see cref="FanStatus"/> and <see cref="FanChannel"/> go on the wire as-is rather than
    /// through separate DTOs, the same way <see cref="GpuStateDto"/> carries
    /// <see cref="Model.Profile"/> and <see cref="Rules.Rule"/> directly. Both are plain records
    /// of primitives, and a parallel set of types would only be somewhere for the two to drift.
    ///
    /// These default to empty, so an old client talking to a new service ignores them and a new
    /// client talking to an old service simply sees no fans.
    /// </summary>
    public IReadOnlyList<FanStatus> Fans { get; init; } = [];

    /// <summary>Every writable header on the board, adopted or not. Populated by list-fans, which
    /// is how a user finds out which index is the one they care about.</summary>
    public IReadOnlyList<FanChannel> FanChannels { get; init; } = [];

    public FanHardwareInfo? FanHardware { get; init; }

    /// <summary>Human-readable summary of what a mutating command actually did.</summary>
    public string? Message { get; init; }

    public static IpcResponse Failure(string error, int code) =>
        new() { Ok = false, Error = error, Code = code };

    public static IpcResponse Success(string? message = null) =>
        new() { Ok = true, Message = message };
}
