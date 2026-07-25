using System.Diagnostics;

namespace T4Power.Core.Rules;

/// <summary>Supplies the set of running process names. Abstracted so the rule engine is testable.</summary>
public interface IProcessSource
{
    /// <summary>Lower-cased process names without the <c>.exe</c> suffix.</summary>
    IReadOnlySet<string> GetRunningNames();
}

/// <summary>
/// Enumerates running processes, caching for a short window so a 1 Hz rule tick does not pay
/// for a full process snapshot every time.
///
/// Polling rather than a WMI event subscription: <c>Win32_ProcessStartTrace</c> is meaningfully
/// more machinery, and a one-second reaction time is irrelevant next to the seconds a GPU
/// workload takes to spin up.
/// </summary>
public sealed class ProcessWatcher : IProcessSource
{
    readonly TimeSpan _cacheFor;
    readonly Func<DateTimeOffset> _clock;

    IReadOnlySet<string> _cached = new HashSet<string>();
    DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public ProcessWatcher(TimeSpan? cacheFor = null, Func<DateTimeOffset>? clock = null)
    {
        _cacheFor = cacheFor ?? TimeSpan.FromSeconds(2);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlySet<string> GetRunningNames()
    {
        var now = _clock();
        if (now - _cachedAt < _cacheFor) return _cached;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch (InvalidOperationException) { /* exited between enumeration and read */ }
            finally { p.Dispose(); }
        }

        _cached = names;
        _cachedAt = now;
        return names;
    }

    /// <summary>Normalises "Blender.exe", "blender", " blender.EXE " to "blender".</summary>
    public static string Normalise(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }
}
