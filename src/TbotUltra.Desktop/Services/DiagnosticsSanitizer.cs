using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TbotUltra.Desktop.Services;

internal static partial class DiagnosticsSanitizer
{
    private const string Redacted = "<redacted>";

    internal static string SanitizeJson(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            SanitizeNode(root);
            return root?.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }) ?? "null";
        }
        catch (JsonException)
        {
            return SanitizeText(json);
        }
    }

    internal static string SanitizeText(string text)
    {
        var sanitized = text ?? string.Empty;
        sanitized = ReplaceKnownPersonalValue(sanitized, Environment.UserName, "<redacted-user>");
        sanitized = ReplaceKnownPersonalValue(sanitized, Environment.MachineName, "<redacted-machine>");
        sanitized = ReplaceKnownPersonalValue(sanitized, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<redacted-user-profile>");
        sanitized = EmailRegex().Replace(sanitized, "<redacted-email>");
        sanitized = AccountIdentifierRegex().Replace(sanitized, "<redacted-account>");
        sanitized = JwtRegex().Replace(sanitized, "<redacted-token>");
        sanitized = ProxyCredentialRegex().Replace(sanitized, "$1<redacted-proxy-credentials>@");
        sanitized = ProxyEndpointRegex().Replace(sanitized, "<redacted-proxy-endpoint>");
        sanitized = SensitiveLineRegex().Replace(sanitized, "$1<redacted>");
        return sanitized;
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveKey(property.Key))
                {
                    obj[property.Key] = Redacted;
                }
                else
                {
                    SanitizeNode(property.Value);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            // String values are replaced in-place during sanitization. Enumerate a snapshot so replacing
            // an array item does not invalidate JsonArray's live enumerator.
            foreach (var item in array.ToList())
            {
                SanitizeNode(item);
            }

            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            value.ReplaceWith(SanitizeText(stringValue));
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("username", StringComparison.Ordinal)
            || normalized.Contains("email", StringComparison.Ordinal)
            || normalized.Contains("account", StringComparison.Ordinal)
            || normalized.Contains("proxyserver", StringComparison.Ordinal)
            || normalized.Contains("proxyendpoint", StringComparison.Ordinal)
            || normalized.Contains("proxyusername", StringComparison.Ordinal)
            || normalized.Contains("proxypassword", StringComparison.Ordinal)
            || normalized is "host" or "port";
    }

    private static string ReplaceKnownPersonalValue(string text, string value, string replacement)
        => string.IsNullOrWhiteSpace(value)
            ? text
            : text.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b[A-Z0-9.-]+_(?:gmail|hotmail|outlook|yahoo|protonmail)_(?:com|net|org)(?:_[A-Z0-9.-]+)*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccountIdentifierRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"((?:https?|socks[45]?)://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProxyCredentialRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}:\d{2,5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProxyEndpointRegex();

    [GeneratedRegex("""(?i)(\b(?:password|passwd|secret|token|cookie|authorization|proxy(?:_?(?:username|password|server|endpoint)))\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveLineRegex();
}
