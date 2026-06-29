namespace TbotUltra.Core.Configuration;

/// <summary>
/// Identifies which kind of Travian server the bot is connected to.
/// Used to gate remaining behaviour that only exists on the SS-Travi private server
/// so it stays hidden/disabled on official servers.
/// </summary>
public enum ServerFlavor
{
    /// <summary>
    /// An official Travian (Travian Legends, T4.6) server, e.g. travian.com / travian.de / travian.se.
    /// This is the default when the flavor cannot be determined from configuration.
    /// </summary>
    Official = 0,

    /// <summary>
    /// The SS-Travi private server (*.ss-travi.com). Legacy private-server flavor.
    /// </summary>
    SsTravi = 1,
}

/// <summary>
/// Helpers for resolving the <see cref="ServerFlavor"/> of a server from its URL/host
/// or from an explicit configuration value.
/// </summary>
public static class ServerFlavorDetector
{
    private const string SsTraviHostSuffix = "ss-travi.com";

    /// <summary>
    /// Detects the server flavor from a base URL or bare host name.
    /// Returns <see cref="ServerFlavor.SsTravi"/> when the host is (a subdomain of) ss-travi.com,
    /// otherwise <see cref="ServerFlavor.Official"/>.
    /// </summary>
    public static ServerFlavor FromBaseUrl(string? baseUrlOrHost)
    {
        if (string.IsNullOrWhiteSpace(baseUrlOrHost))
        {
            return ServerFlavor.Official;
        }

        var host = ExtractHost(baseUrlOrHost);
        if (string.IsNullOrEmpty(host))
        {
            return ServerFlavor.Official;
        }

        return host.Equals(SsTraviHostSuffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + SsTraviHostSuffix, StringComparison.OrdinalIgnoreCase)
            ? ServerFlavor.SsTravi
            : ServerFlavor.Official;
    }

    /// <summary>
    /// Parses an explicit configuration string (e.g. "official" / "ss_travi" / "sstravi") into a
    /// <see cref="ServerFlavor"/>. Returns <c>null</c> when the value is missing or unrecognized,
    /// so callers can fall back to auto-detection.
    /// </summary>
    public static ServerFlavor? ParseExplicit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "official" or "legends" or "travian" => ServerFlavor.Official,
            "sstravi" or "ss_travi" or "ss-travi" or "private" => ServerFlavor.SsTravi,
            _ => null,
        };
    }

    private static string ExtractHost(string value)
    {
        var candidate = value.Trim();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return absolute.Host;
        }

        // Not an absolute URI (bare host, or missing scheme) – strip any path/scheme manually.
        var schemeSeparator = candidate.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            candidate = candidate[(schemeSeparator + 3)..];
        }

        var slash = candidate.IndexOf('/');
        if (slash >= 0)
        {
            candidate = candidate[..slash];
        }

        var colon = candidate.IndexOf(':');
        if (colon >= 0)
        {
            candidate = candidate[..colon];
        }

        return candidate;
    }
}
