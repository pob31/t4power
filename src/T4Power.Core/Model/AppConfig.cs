using System.Text.Json.Serialization;

namespace T4Power.Core.Model;

/// <summary>
/// A manual override in force for one GPU. Set by the slider, a preset button, or the CLI.
/// Beats the rule engine until it is cleared or expires.
/// </summary>
public sealed record Override
{
    /// <summary>Name of the profile to hold, or null when <see cref="PowerLimitW"/>/<see cref="LockClocks"/>
    /// carry an ad-hoc setting. Serialised as "profile" to keep config.json terse.</summary>
    [JsonPropertyName("profile")]
    public string? ProfileName { get; init; }

    public double? PowerLimitW { get; init; }
    public ClockLock? LockClocks { get; init; }

    /// <summary>True when the ad-hoc setting explicitly wants clocks unlocked, as opposed to
    /// <see cref="LockClocks"/> simply being absent and meaning "leave alone".</summary>
    public bool UnlockClocks { get; init; }

    /// <summary>
    /// When this override lapses back to the rule engine. Null means it holds indefinitely.
    /// Expiry is evaluated by the service on its own tick, so a TTL still fires if the client
    /// that set it has gone away.
    /// </summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is not null && now >= ExpiresUtc;
}

/// <summary>Per-GPU settings, keyed by UUID.</summary>
public sealed record GpuConfig
{
    public required string Uuid { get; init; }

    /// <summary>Remembered for display when the GPU is absent (removed, or driver not loaded).</summary>
    public string? FriendlyName { get; init; }

    /// <summary>When false, T4Power reports on this GPU but never writes to it.</summary>
    public bool Managed { get; init; } = true;

    /// <summary>Applied at service start, and whenever no rule matches.</summary>
    public string DefaultProfile { get; init; } = Profile.Eco;

    public IReadOnlyList<Profile> Profiles { get; init; } = [];
    public IReadOnlyList<Rule> Rules { get; init; } = [];
    public Override? Override { get; init; }

    /// <summary>
    /// Force the Eco profile above this temperature regardless of rules. The T4 is passively
    /// cooled and depends entirely on chassis airflow, so a backstop is worth having.
    /// Null disables the guard.
    /// </summary>
    public uint? ThermalGuardC { get; init; }

    public Profile? FindProfile(string? name) => name is null
        ? null
        : Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static GpuConfig CreateDefault(GpuInfo info) => new()
    {
        Uuid = info.Uuid,
        FriendlyName = info.Name,
        // Only the T4 is managed out of the box. Silently clamping someone's display GPU
        // because the app happened to enumerate it would be a nasty surprise.
        Managed = info.IsTeslaT4,
        DefaultProfile = Profile.Eco,
        Profiles = Profile.DefaultsFor(info),
        Rules = Rule.Defaults(),
        ThermalGuardC = info.IsTeslaT4 ? 85u : null,
    };
}

public sealed record AppConfig
{
    /// <summary>
    /// Current schema version. Bumped to 2 when the Max profile changed from "clocks unlocked"
    /// to "clocks pinned at the top": unlocked lets an idle card sit in P8, so the old Max could
    /// not raise performance at all.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Poll interval for telemetry and rule evaluation.</summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>
    /// On a T4 the card can sit wedged at P0/1590 MHz burning 36 W with nothing running. A
    /// lock-then-unlock cycle at service start knocks it back down to P8. Measured on driver
    /// 610.74; harmless on GPUs that do not need it.
    /// </summary>
    public bool KickStuckPState { get; init; } = true;

    /// <summary>
    /// SIDs allowed to talk to the control pipe, on top of LocalSystem and Administrators.
    /// <c>--install-service</c> records the installing user here, which is what lets the tray UI
    /// and CLI work unelevated.
    ///
    /// This is the app's privilege boundary. Granting <c>BUILTIN\Users</c> would let any account
    /// on the machine change GPU power, so the default is deliberately just the one user.
    /// </summary>
    public IReadOnlyList<string> PipeAllowedSids { get; init; } = [];

    public IReadOnlyList<GpuConfig> Gpus { get; init; } = [];

    public GpuConfig? Find(string uuid) =>
        Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a <see cref="GpuConfig"/> for any GPU we have not seen before, preserving existing
    /// entries untouched. Config is keyed by UUID, so a GPU that disappears keeps its settings
    /// for when it comes back.
    /// </summary>
    public AppConfig EnsureEntriesFor(IEnumerable<GpuInfo> discovered)
    {
        var known = Gpus.ToList();
        var added = false;

        foreach (var info in discovered)
        {
            if (known.Any(g => string.Equals(g.Uuid, info.Uuid, StringComparison.OrdinalIgnoreCase)))
                continue;
            known.Add(GpuConfig.CreateDefault(info));
            added = true;
        }

        return added ? this with { Gpus = known } : this;
    }

    public AppConfig WithGpu(GpuConfig updated) => this with
    {
        Gpus = Gpus.Select(g =>
            string.Equals(g.Uuid, updated.Uuid, StringComparison.OrdinalIgnoreCase) ? updated : g).ToList(),
    };

    /// <summary>
    /// Brings a config written by an older version up to date. Rules, watchlists and the managed
    /// flag are preserved; only the stock profiles are regenerated, because a v1 config carries a
    /// Max profile that unlocks clocks and therefore cannot raise performance.
    ///
    /// Profiles the user added under their own names are left alone.
    /// </summary>
    public AppConfig Migrate(IReadOnlyDictionary<string, GpuInfo> discovered, Action<string>? log = null)
    {
        if (Version >= CurrentVersion) return this;

        log?.Invoke($"migrating configuration from v{Version} to v{CurrentVersion}: " +
                    "regenerating the stock Eco/Balanced/Max profiles");

        var updated = Gpus.Select(gpu =>
        {
            if (!discovered.TryGetValue(gpu.Uuid, out var info)) return gpu;

            var stock = Profile.DefaultsFor(info);
            var stockNames = stock.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var custom = gpu.Profiles.Where(p => !stockNames.Contains(p.Name));

            return gpu with { Profiles = [.. stock, .. custom] };
        }).ToList();

        return this with { Version = CurrentVersion, Gpus = updated };
    }
}
