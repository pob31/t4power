using System.Globalization;
using System.Text.RegularExpressions;

namespace T4Power.Cli;

public enum Verb
{
    TrayUi,
    Service,
    InstallService,
    UninstallService,
    List,
    Status,
    Set,
    Profile,
    Auto,
    RestoreDefaults,
    Help,
    Invalid,
}

/// <summary>What to do with the GPU clock lock. Distinct from "leave it alone" (null).</summary>
public sealed record ClockRequest(bool Unlock, uint MinMhz = 0, uint MaxMhz = 0)
{
    public static ClockRequest Unlocked => new(Unlock: true);
    public static ClockRequest Range(uint min, uint max) => new(false, min, max);
    public override string ToString() => Unlock ? "unlock" : $"{MinMhz}-{MaxMhz} MHz";
}

public sealed record CommandLineOptions
{
    public Verb Verb { get; init; } = Verb.TrayUi;

    /// <summary>UUID, index, or a case-insensitive substring of the GPU name. Null means
    /// "every managed GPU".</summary>
    public string? GpuSelector { get; init; }

    public double? PowerW { get; init; }
    public ClockRequest? Clocks { get; init; }

    /// <summary>TTL after which the override auto-reverts to the rule engine. The service
    /// enforces expiry on its own tick, so it survives the client dying.</summary>
    public TimeSpan? Duration { get; init; }

    public string? ProfileName { get; init; }
    public bool Json { get; init; }

    /// <summary>
    /// Internal: the SID to grant control-pipe access during install. Carried across the UAC
    /// elevation because by the time we are elevated the current user is an administrator, not
    /// the person who asked. Not documented in the usage text.
    /// </summary>
    public string? AclSid { get; init; }

    public string? Error { get; init; }
}

public static partial class CommandLine
{
    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 0) return new CommandLineOptions { Verb = Verb.TrayUi };

        var verb = Verb.Invalid;
        string? gpu = null, profile = null, error = null, aclSid = null;
        double? power = null;
        ClockRequest? clocks = null;
        TimeSpan? duration = null;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].TrimStart('-', '/').ToLowerInvariant();

            string? Next(string name)
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) return args[++i];
                error ??= $"--{name} requires a value";
                return null;
            }

            switch (a)
            {
                case "service": verb = Verb.Service; break;
                case "install-service" or "install": verb = Verb.InstallService; break;
                case "uninstall-service" or "uninstall": verb = Verb.UninstallService; break;
                case "list": verb = Verb.List; break;
                case "status": verb = Verb.Status; break;
                case "set": verb = Verb.Set; break;
                case "auto": verb = Verb.Auto; break;
                case "restore-defaults": verb = Verb.RestoreDefaults; break;
                case "tray" or "ui": verb = Verb.TrayUi; break;
                case "help" or "h" or "?": verb = Verb.Help; break;

                case "profile":
                    // --profile doubles as a verb and as an argument to --set.
                    profile = Next("profile");
                    if (verb is Verb.Invalid) verb = Verb.Profile;
                    break;

                case "gpu" or "uuid" or "i":
                    gpu = Next("gpu");
                    break;

                case "power" or "pl" or "watts":
                    var pw = Next("power");
                    if (pw is not null)
                    {
                        if (double.TryParse(pw, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                            power = w;
                        else error ??= $"--power expects watts, got '{pw}'";
                    }
                    break;

                case "clocks" or "lgc":
                    var cv = Next("clocks");
                    if (cv is not null)
                    {
                        clocks = ParseClocks(cv, ref error);
                    }
                    break;

                case "for" or "duration":
                    var dv = Next("for");
                    if (dv is not null)
                    {
                        duration = ParseDuration(dv);
                        if (duration is null) error ??= $"--for expects a duration like 30m, 2h, 90s; got '{dv}'";
                    }
                    break;

                case "acl-sid":
                    aclSid = Next("acl-sid");
                    break;

                case "json": json = true; break;
                case "version" or "v": verb = Verb.Help; break;

                default:
                    error ??= $"unknown argument '{args[i]}'";
                    break;
            }
        }

        if (verb == Verb.Invalid) error ??= "no command given";

        return new CommandLineOptions
        {
            Verb = error is null ? verb : Verb.Invalid,
            GpuSelector = gpu,
            PowerW = power,
            Clocks = clocks,
            Duration = duration,
            ProfileName = profile,
            Json = json,
            AclSid = aclSid,
            Error = error,
        };
    }

    /// <summary>Accepts <c>unlock</c>/<c>reset</c>, a range <c>300-900</c>, or a single value.</summary>
    static ClockRequest? ParseClocks(string value, ref string? error)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "unlock" or "unlocked" or "reset" or "off" or "none") return ClockRequest.Unlocked;

        var m = ClockRangeRegex().Match(v);
        if (m.Success)
        {
            var min = uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var max = m.Groups[2].Success
                ? uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                : min;
            return ClockRequest.Range(Math.Min(min, max), Math.Max(min, max));
        }

        error ??= $"--clocks expects 'unlock', '300-900', or a single MHz value; got '{value}'";
        return null;
    }

    /// <summary>Bare numbers are seconds; <c>s</c>/<c>m</c>/<c>h</c>/<c>d</c> suffixes supported.</summary>
    public static TimeSpan? ParseDuration(string value)
    {
        var m = DurationRegex().Match(value.Trim().ToLowerInvariant());
        if (!m.Success) return null;

        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var span = m.Groups[2].Value switch
        {
            "s" or "" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            _ => (TimeSpan?)null,
        };
        return span is { TotalSeconds: > 0 } ? span : null;
    }

    [GeneratedRegex(@"^(\d+)\s*(?:[-,:]\s*(\d+))?$")]
    private static partial Regex ClockRangeRegex();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(s|m|h|d)?$")]
    private static partial Regex DurationRegex();

    public static void PrintUsage()
    {
        var version = typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Console.WriteLine($"""
            T4Power {version} — power and clock manager for NVIDIA GPUs

            USAGE
              T4Power                              Start the tray UI (no elevation needed)
              T4Power --list [--json]              Discovered GPUs, UUIDs and capabilities
              T4Power --status [--gpu X] [--json]  Live telemetry, active profile and rule
              T4Power --profile <name> [--gpu X] [--for 30m]
              T4Power --set [--gpu X] [--power 65] [--clocks 300-900|unlock] [--for 45m]
              T4Power --auto [--gpu X]             Drop the override, hand back to the rules
              T4Power --restore-defaults [--gpu X] Default power limit, clocks unlocked

              T4Power --install-service            Register the startup service (prompts for UAC)
              T4Power --uninstall-service          Remove it and restore GPU defaults

            OPTIONS
              --gpu <sel>     UUID, index, or part of the name. Default: all managed GPUs.
              --power <W>     Power limit in watts; clamped to what the GPU reports.
              --clocks <r>    '300-900', a single MHz value, or 'unlock'.
              --for <d>       Auto-revert after 30m / 2h / 90s. Enforced by the service, so
                              it still expires if the caller goes away.
              --json          Machine-readable output; errors go to stderr as JSON.

            EXIT CODES
              0 ok   1 usage   2 service unavailable   3 unknown gpu   4 out of range
              5 permission denied   6 not supported   7 nvml unavailable   8 failed
            """);
    }
}
