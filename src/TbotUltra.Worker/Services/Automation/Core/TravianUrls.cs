using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless URL/path helpers extracted from <see cref="TravianClient"/>:
/// newdid parsing/canonicalization of village-switch URLs, build-slot id extraction,
/// and filesystem-safe path segments. Pure functions so they can be unit-tested in isolation.
/// </summary>
internal static class TravianUrls
{
    private static readonly Regex NewdidUrlRegex =
        new(@"[?&]newdid=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Switching village is driven solely by the newdid. Stored/sidebar URLs can pick up extra query
    // params from the page they were read on (e.g. a build slot's "id=10"), and a URL like
    // dorf1.php?newdid=X&id=10 must reduce to the canonical dorf1.php?newdid=X so the switch never
    // hits the site root (served as the login page).
    internal static string CanonicalizeVillageSwitchUrl(string url)
    {
        var newdid = TryParseNewdid(url);
        return newdid is int id ? $"dorf1.php?newdid={id}" : url;
    }

    // Stable village id parsed from a switch URL (dorf1.php?newdid=X). Used to match villages across
    // reads without depending on the (mutable) village name.
    internal static int? TryParseNewdid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = NewdidUrlRegex.Match(url);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    internal static int? ExtractSlotIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = Regex.Match(url, @"[?&]id=(\d+)");
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var slotId)
            ? slotId
            : null;
    }

    // True only when the page is actually the build-slot page (build.php?id=slot). Other pages such as
    // dorf2.php?id=slot&gid=N carry the same id= query param, so an id match alone is NOT enough to prove
    // we are on the upgrade page — checking for build.php prevents reading/clicking the upgrade button on
    // the village overview, which silently leaves the bot stuck on dorf2 ("could not find Upgrade to level").
    internal static bool IsBuildPageForSlot(string? url, int slotId)
    {
        return !string.IsNullOrWhiteSpace(url)
            && url.Contains("build.php", StringComparison.OrdinalIgnoreCase)
            && ExtractSlotIdFromUrl(url) == slotId;
    }

    internal static string SafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "artifact";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "artifact"
            : sanitized;
    }
}
