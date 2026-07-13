using System.Text.Json;

namespace TbotUltra.Desktop.Services;

internal sealed record ProxyCheckInfo(string Ip, string Location, string Isp);
internal sealed record ProxyCheckResult(string Ip, string Location, string Isp, string Route, string Latency);
internal sealed record ProxyCheckFailure(string Error, string Route, string Target);

/// <summary>
/// Stateless encoding and decoding for account proxy-check results.
/// </summary>
internal static class ProxyCheckResultCodec
{
    internal const string LookupTarget = "https://ipwho.is/";

    internal static string BuildSuccess(ProxyCheckInfo info, string route, string latency)
        => JsonSerializer.Serialize(new ProxyCheckResult(info.Ip, info.Location, info.Isp, route, latency));

    internal static string BuildFailure(string error, string route)
        => JsonSerializer.Serialize(new ProxyCheckFailure(error, route, LookupTarget));

    internal static bool TryParseSuccess(string raw, out ProxyCheckResult result)
    {
        try
        {
            result = JsonSerializer.Deserialize<ProxyCheckResult>(raw)
                ?? new ProxyCheckResult("unknown", "unknown", "unknown", "unknown", "unknown");
            return true;
        }
        catch
        {
            result = new ProxyCheckResult("unknown", "unknown", "unknown", "unknown", "unknown");
            return false;
        }
    }

    internal static bool TryParseFailure(string raw, out ProxyCheckFailure failure)
    {
        try
        {
            failure = JsonSerializer.Deserialize<ProxyCheckFailure>(raw)
                ?? new ProxyCheckFailure("unknown", "unknown", LookupTarget);
            return true;
        }
        catch
        {
            failure = new ProxyCheckFailure(raw, "unknown", LookupTarget);
            return false;
        }
    }

    internal static string SummarizeError(string? message)
    {
        var value = message ?? string.Empty;
        var firstLine = value.Replace("\r", string.Empty).Split('\n').FirstOrDefault() ?? string.Empty;
        return firstLine.Length == 0 ? "Unknown error." : firstLine;
    }

    internal static ProxyCheckInfo ParseLookupResponse(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var success = !root.TryGetProperty("success", out var successNode) || successNode.GetBoolean();
            if (!success)
            {
                var message = ReadString(root, "message");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "IP lookup failed." : message);
            }

            var ip = ReadString(root, "ip");
            var country = ReadString(root, "country");
            var region = ReadString(root, "region");
            var city = ReadString(root, "city");
            var isp = ReadString(root, "isp");
            var location = string.Join(", ", new[] { city, region, country }.Where(item => !string.IsNullOrWhiteSpace(item)));
            return new ProxyCheckInfo(
                string.IsNullOrWhiteSpace(ip) ? "unknown" : ip,
                string.IsNullOrWhiteSpace(location) ? "unknown" : location,
                string.IsNullOrWhiteSpace(isp) ? "unknown" : isp);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"IP lookup returned invalid data: {ex.Message}");
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
    }
}
