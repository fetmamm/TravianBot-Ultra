using Microsoft.Playwright;

namespace TbotUltra.Worker.Infrastructure;

/// <summary>
/// Pure helpers for the per-account proxy feature: turn a single user-entered server string into a
/// Playwright <see cref="Proxy"/>, mask credentials for logging, and recognise Chromium proxy-failure
/// error codes. Kept stateless so the rules are shared by runtime + unit tests.
/// </summary>
public static class ProxyParser
{
    // Chromium proxy-failure codes surfaced inside Playwright navigation exception messages. When one
    // of these appears we know the failure is the proxy, not Travian, and can say so clearly.
    private static readonly string[] ProxyErrorMarkers =
    {
        "ERR_PROXY_CONNECTION_FAILED",
        "ERR_TUNNEL_CONNECTION_FAILED",
        "ERR_PROXY_AUTH_REQUESTED",
        "ERR_PROXY_AUTH_UNSUPPORTED",
        "ERR_NO_SUPPORTED_PROXIES",
        "ERR_MANDATORY_PROXY_CONFIGURATION_FAILED",
        "ERR_SOCKS_CONNECTION_FAILED",
    };

    /// <summary>
    /// Builds a Playwright <see cref="Proxy"/> from a single server string. Accepts a bare
    /// <c>host:port</c>, a scheme-qualified <c>scheme://host:port</c>, and optional inline credentials
    /// <c>user:pass@host:port</c> (Playwright requires those split off into Username/Password).
    /// Returns false for an empty/whitespace string or one without a host. <paramref name="warning"/>
    /// carries a non-fatal note to log (e.g. SOCKS auth is unsupported by Chromium); the proxy is still
    /// returned in that case.
    /// </summary>
    public static bool TryBuild(string? serverString, out Proxy? proxy, out string? warning)
    {
        proxy = null;
        warning = null;

        var value = serverString?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        // Peel off an optional scheme first so credential parsing never trips on the "://".
        var scheme = string.Empty;
        var rest = value;
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            scheme = value[..(schemeIndex + 3)]; // include the "://"
            rest = value[(schemeIndex + 3)..];
        }

        string? username = null;
        string? password = null;
        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            var credentials = rest[..atIndex];
            rest = rest[(atIndex + 1)..];

            var colonIndex = credentials.IndexOf(':');
            if (colonIndex >= 0)
            {
                username = credentials[..colonIndex];
                password = credentials[(colonIndex + 1)..];
            }
            else
            {
                username = credentials;
            }
        }

        if (rest.Length == 0)
        {
            // String had only credentials, no host — treat as not configured.
            return false;
        }

        // Chromium cannot authenticate SOCKS proxies with username/password.
        if (scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password)))
        {
            warning = "SOCKS proxy auth is not supported by Chromium; credentials will be ignored.";
        }

        proxy = new Proxy { Server = scheme + rest };
        if (!string.IsNullOrEmpty(username))
        {
            proxy.Username = username;
        }
        if (!string.IsNullOrEmpty(password))
        {
            proxy.Password = password;
        }

        return true;
    }

    /// <summary>
    /// Returns the server string with any inline password replaced by <c>***</c> so it is safe to log.
    /// </summary>
    public static string MaskForLog(string? serverString)
    {
        var value = serverString?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "(empty)";
        }

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeIndex >= 0 ? value[..(schemeIndex + 3)] : string.Empty;
        var rest = schemeIndex >= 0 ? value[(schemeIndex + 3)..] : value;

        var atIndex = rest.LastIndexOf('@');
        if (atIndex < 0)
        {
            return value; // no credentials to mask
        }

        var credentials = rest[..atIndex];
        var host = rest[(atIndex + 1)..];
        var colonIndex = credentials.IndexOf(':');
        var maskedCredentials = colonIndex >= 0 ? $"{credentials[..colonIndex]}:***" : credentials;
        return $"{scheme}{maskedCredentials}@{host}";
    }

    /// <summary>
    /// True when an exception/navigation message contains a known Chromium proxy-failure code.
    /// </summary>
    public static bool LooksLikeProxyError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var marker in ProxyErrorMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
