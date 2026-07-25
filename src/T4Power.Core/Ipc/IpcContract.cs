using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>Human-readable summary of what a mutating command actually did.</summary>
    public string? Message { get; init; }

    public static IpcResponse Failure(string error, int code) =>
        new() { Ok = false, Error = error, Code = code };

    public static IpcResponse Success(string? message = null) =>
        new() { Ok = true, Message = message };
}
