using System.Globalization;
using System.Text.RegularExpressions;
using T4Power.Core.Model;

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
    Watch,
    Unwatch,
    Rules,

    // Fan control.
    Fans,
    AdoptFan,
    ReleaseFan,
    FanSet,
    FanAuto,
    FanCurve,
    IdentifyFan,

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

    /// <summary>Open the main window on launch instead of starting minimised to the tray.</summary>
    public bool ShowWindow { get; init; }

    /// <summary>Executable names for the watchlist verbs.</summary>
    public IReadOnlyList<string> Match { get; init; } = [];

    /// <summary>Fan header selector: full identifier, its trailing segment, an index, or part of
    /// the name. Required by every fan verb except <see cref="Verb.Fans"/>.</summary>
    public string? FanSelector { get; init; }

    /// <summary>Duty for --fan-set and --identify-fan, 0-100.</summary>
    public double? Percent { get; init; }

    /// <summary>Curve points from --points, already parsed. Null means "show, do not set".</summary>
    public IReadOnlyList<FanCurvePoint>? CurvePoints { get; init; }

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
        string? gpu = null, profile = null, error = null, aclSid = null, fan = null;
        var showWindow = false;
        var match = new List<string>();
        double? power = null, percent = null;
        ClockRequest? clocks = null;
        TimeSpan? duration = null;
        IReadOnlyList<FanCurvePoint>? points = null;
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
                case "tray": verb = Verb.TrayUi; break;

                case "window" or "open" or "ui":
                    // Same tray app, but with the window up front — for launching from a
                    // shortcut or the taskbar rather than at login.
                    verb = Verb.TrayUi;
                    showWindow = true;
                    break;
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

                case "watch" or "unwatch":
                    verb = a == "watch" ? Verb.Watch : Verb.Unwatch;
                    // Accept several names in one go: --watch ollama.exe blender.exe
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        match.Add(args[++i]);
                    if (match.Count == 0) error ??= $"--{a} needs at least one executable name";
                    break;

                case "rules":
                    verb = Verb.Rules;
                    break;

                // ---- fan control ----------------------------------------------------
                //
                // Each verb takes its selector inline (--adopt-fan control/3) because that reads
                // better than a separate --fan, but --fan is accepted too so the two styles can
                // be mixed in scripts.

                case "fans":
                    verb = Verb.Fans;
                    break;

                case "adopt-fan":
                    verb = Verb.AdoptFan;
                    fan = Next("adopt-fan");
                    break;

                case "release-fan":
                    verb = Verb.ReleaseFan;
                    fan = Next("release-fan");
                    break;

                case "fan-set":
                    verb = Verb.FanSet;
                    fan = Next("fan-set");
                    break;

                case "fan-auto":
                    verb = Verb.FanAuto;
                    fan = Next("fan-auto");
                    break;

                case "fan-curve":
                    verb = Verb.FanCurve;
                    fan = Next("fan-curve");
                    break;

                case "identify-fan":
                    verb = Verb.IdentifyFan;
                    fan = Next("identify-fan");
                    break;

                case "fan":
                    fan = Next("fan");
                    break;

                case "percent" or "duty":
                    var pc = Next("percent");
                    if (pc is not null)
                    {
                        if (double.TryParse(pc, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                            percent = p;
                        else error ??= $"--percent expects 0-100, got '{pc}'";
                    }
                    break;

                case "points":
                    var pv = Next("points");
                    if (pv is not null) points = ParsePoints(pv, ref error);
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
            ShowWindow = showWindow,
            Match = match,
            FanSelector = fan,
            Percent = percent,
            CurvePoints = points,
            AclSid = aclSid,
            Error = error,
        };
    }

    /// <summary>Defers to <see cref="FanCurve.TryParsePoints"/>, which lives in Core so it can be
    /// tested without pulling the executable into the test project.</summary>
    static IReadOnlyList<FanCurvePoint>? ParsePoints(string value, ref string? error)
    {
        if (FanCurve.TryParsePoints(value, out var points, out var why)) return points;

        error ??= $"--points {why}";
        return null;
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

              T4Power --rules [--gpu X]            Show the auto-switch rules
              T4Power --watch ollama.exe blender.exe [--gpu X] [--profile Max]
              T4Power --unwatch ollama.exe [--gpu X]

              T4Power --install-service            Register the startup service (prompts for UAC)
              T4Power --uninstall-service          Remove it and restore GPU defaults

            MOTHERBOARD FAN CONTROL          (needs the PawnIO driver — see the README)
              T4Power --fans [--json]              Every fan header, with live RPM and duty
              T4Power --identify-fan <sel> [--for 10s]
                                                   Spin one up so you can hear which it is
              T4Power --adopt-fan <sel> --gpu T4   Drive that header from that GPU's temperature
              T4Power --fan-curve <sel> [--points "49.5:27,62.8:100"]
                                                   Show the curve, or set it
              T4Power --fan-set <sel> --percent 60 [--for 10m]
              T4Power --fan-auto <sel>             Drop the manual duty, back to the curve
              T4Power --release-fan <sel>          Hand it back to the BIOS and stop managing it

            OPTIONS
              --gpu <sel>     UUID, index, or part of the name. Default: all managed GPUs.
              --power <W>     Power limit in watts; clamped to what the GPU reports.
              --clocks <r>    '300-900', a single MHz value, or 'unlock'.
              --for <d>       Auto-revert after 30m / 2h / 90s. Enforced by the service, so
                              it still expires if the caller goes away.
              --fan <sel>     Fan header: full identifier, 'control/3', an index, or part of
                              the name. Unlike --gpu, it never means "all".
              --percent <p>   Fan duty, 0-100; clamped to what the chip accepts.
              --points <list> Curve as 'temp:percent' pairs, e.g. "49.5:27,62.8:100".
              --json          Machine-readable output; errors go to stderr as JSON.

            EXIT CODES
              0 ok   1 usage   2 service unavailable   3 unknown gpu or fan   4 out of range
              5 permission denied   6 not supported   7 nvml unavailable   8 failed

            T4Power {version}  Copyright (C) 2026 Pierre-Olivier Boulant
            This program comes with ABSOLUTELY NO WARRANTY. It is free software, and you are
            welcome to redistribute it under the terms of the GNU General Public License,
            version 3 or later. See <https://www.gnu.org/licenses/gpl-3.0.html>.
            """);
    }
}
