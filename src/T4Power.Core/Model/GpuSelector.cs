namespace T4Power.Core.Model;

/// <summary>
/// Resolves a user-supplied GPU selector. UUID is the canonical form; index and name-substring
/// are conveniences for humans typing at a prompt. Scripts should always use the UUID, since
/// index order is not stable.
/// </summary>
public static class GpuSelector
{
    public static bool Matches(GpuInfo info, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return true; // no selector means "all"

        var s = selector.Trim();

        if (string.Equals(info.Uuid, s, StringComparison.OrdinalIgnoreCase)) return true;

        // Allow the UUID without the "GPU-" prefix, and any unambiguous leading portion of it.
        var bare = info.Uuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) ? info.Uuid[4..] : info.Uuid;
        var sBare = s.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
        if (sBare.Length >= 8 && bare.StartsWith(sBare, StringComparison.OrdinalIgnoreCase)) return true;

        if (int.TryParse(s, out var index) && index == info.Index) return true;

        return info.Name.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<T> Filter<T>(IEnumerable<T> items, Func<T, GpuInfo> info, string? selector) =>
        items.Where(i => Matches(info(i), selector));
}
