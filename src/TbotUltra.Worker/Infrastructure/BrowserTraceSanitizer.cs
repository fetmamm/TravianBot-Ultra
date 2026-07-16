using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Infrastructure;

public static partial class BrowserTraceSanitizer
{
    private const int MaxFieldLength = 1200;
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "api_key", "auth", "authorization", "code", "cookie", "csrf", "hash", "jwt",
        "key", "password", "passwd", "refresh_token", "session", "sid", "state", "ticket", "token", "wuid",
    };

    public static string SanitizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return SanitizeText(value);
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = SanitizeQuery(uri.Query),
            Fragment = string.Empty,
        };
        return SanitizeText(builder.Uri.ToString());
    }

    public static string SanitizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var sanitized = ProxyCredentialRegex().Replace(value, "$1<redacted>@");
        sanitized = SecretAssignmentRegex().Replace(
            sanitized,
            match => $"{match.Groups[1].Value}{match.Groups[2].Value}<redacted>");
        sanitized = BearerTokenRegex().Replace(sanitized, "Bearer <redacted>");
        sanitized = EmailAddressRegex().Replace(sanitized, "<redacted-email>");
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Replace("'", "''").Trim();
        return sanitized.Length <= MaxFieldLength
            ? sanitized
            : sanitized[..MaxFieldLength] + "…";
    }

    public static string FormatInputValue(string fieldName, string? value)
    {
        var length = value?.Length ?? 0;
        if (IsSensitiveField(fieldName))
        {
            return $"value=<redacted> length={length}";
        }

        var trimmed = value?.Trim() ?? string.Empty;
        if (SafeOperationalValueRegex().IsMatch(trimmed))
        {
            return $"value={SanitizeText(trimmed)} length={length}";
        }

        return $"value=<omitted> length={length}";
    }

    public static bool IsSensitiveField(string? fieldName)
        => !string.IsNullOrWhiteSpace(fieldName)
           && SensitiveFieldRegex().IsMatch(fieldName);

    private static string SanitizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("&", parts.Select(part =>
        {
            var separator = part.IndexOf('=');
            var rawKey = separator >= 0 ? part[..separator] : part;
            var rawValue = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            var key = Uri.UnescapeDataString(rawKey);
            return SensitiveQueryKeys.Contains(key) || IsSensitiveField(key)
                ? $"{rawKey}=<redacted>"
                : separator >= 0 ? $"{rawKey}={rawValue}" : rawKey;
        }));
    }

    [GeneratedRegex("(?i)\\b(password|passwd|token|access[_-]?token|refresh[_-]?token|csrf|cookie|authorization|proxy[_-]?(?:user|pass(?:word)?))([\\s:=]+)([^\\s,;]+)")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("(?i)(https?://)([^/@\\s]+)@")]
    private static partial Regex ProxyCredentialRegex();

    [GeneratedRegex("(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b")]
    private static partial Regex EmailAddressRegex();

    [GeneratedRegex("(?i)(password|passwd|token|secret|cookie|authorization|csrf|credential|email|username|message|subject)")]
    private static partial Regex SensitiveFieldRegex();

    [GeneratedRegex("^(?:-?\\d+(?:[.,]\\d+)?|[-+]?\\d+\\s*[|:,/]\\s*[-+]?\\d+|true|false|on|off|yes|no)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeOperationalValueRegex();
}
