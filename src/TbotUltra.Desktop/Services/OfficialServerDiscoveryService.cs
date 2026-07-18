using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public static class OfficialServerDiscoveryService
{
    internal static readonly Uri CalendarUri = new("https://lobby.legends.travian.com/api/calendar");
    internal static readonly Uri MetadataUri = new("https://lobby.legends.travian.com/api/metadata");

    private static readonly HttpClient Http = CreateClient();

    public static async Task<List<ServerOption>> FetchSpecialServersAsync(CancellationToken cancellationToken)
    {
        var payloads = await Task.WhenAll(
            FetchJsonAsync(MetadataUri, cancellationToken),
            FetchJsonAsync(CalendarUri, cancellationToken));
        var servers = ParseSpecialServers(payloads.Where(payload => payload is not null).Select(payload => payload!));
        Debug.WriteLine($"[official-server-discovery] Loaded {servers.Count} special gameworld(s).");
        return servers;
    }

    internal static List<ServerOption> ParseSpecialServers(string json)
        => ParseSpecialServers([json]);

    internal static List<ServerOption> ParseSpecialServers(IEnumerable<string> payloads, DateTimeOffset? now = null)
    {
        var servers = new List<ServerOption>();
        var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nowUnixSeconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        foreach (var json in payloads)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Travian server response was not an array.");
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("end", out var endNode)
                    && endNode.ValueKind == JsonValueKind.Number
                    && endNode.TryGetInt64(out var endUnixSeconds)
                    && endUnixSeconds <= nowUnixSeconds)
                {
                    continue;
                }

                if (!entry.TryGetProperty("metadata", out var metadata)
                    || metadata.ValueKind != JsonValueKind.Object
                    || !metadata.TryGetProperty("url", out var urlNode)
                    || urlNode.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlNode.GetString()?.Trim() ?? string.Empty;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.EndsWith(".travian.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = metadata.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String
                    ? typeNode.GetString() ?? string.Empty
                    : string.Empty;
                if (string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase)
                    && OfficialServerCatalog.IsStandardWorldUrl(url))
                {
                    continue;
                }

                var baseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                if (!knownUrls.Add(baseUrl))
                {
                    continue;
                }

                var name = metadata.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                    ? nameNode.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
                servers.Add(new ServerOption
                {
                    Name = name.Length > 0 ? name : uri.Host,
                    BaseUrl = baseUrl,
                    Group = OfficialServerCatalog.SpecialGroupName,
                });
            }
        }

        return servers;
    }

    private static async Task<string?> FetchJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            Debug.WriteLine($"[official-server-discovery] Fetching {uri}");
            using var response = await Http.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[official-server-discovery] Could not fetch {uri}: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TbotUltra", "1.0"));
        return client;
    }
}
