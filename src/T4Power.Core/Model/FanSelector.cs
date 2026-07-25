namespace T4Power.Core.Model;

/// <summary>
/// Resolves a user-supplied fan selector. The full control identifier is the canonical form; the
/// trailing segment, the index and a name substring are conveniences for humans typing at a
/// prompt, since <c>/lpc/nct6701d/control/3</c> is not a thing anyone wants to retype.
///
/// Scripts should use the full identifier: index order depends on chip enumeration and is no more
/// stable than a GPU index.
/// </summary>
public static class FanSelector
{
    public static bool Matches(string controlIdentifier, string? friendlyName, int index, string? selector)
    {
        // Unlike GpuSelector, an empty selector matches nothing rather than everything. "Apply
        // this to every fan I did not name" is not a gesture worth making easy.
        if (string.IsNullOrWhiteSpace(selector)) return false;

        var s = selector.Trim();

        if (string.Equals(controlIdentifier, s, StringComparison.OrdinalIgnoreCase)) return true;

        // "control/3", which is what people read off --fans. Anchored on a slash so that "3" can
        // never match ".../control/13".
        var tail = "/" + s.TrimStart('/');
        if (controlIdentifier.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return true;

        if (int.TryParse(s, out var wanted) && wanted == index) return true;

        return friendlyName is not null && friendlyName.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(FanConfig config, string? selector) =>
        Matches(config.ControlIdentifier, config.FriendlyName, config.ControlIndex, selector);
}
