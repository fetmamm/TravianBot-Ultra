using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop.Services;

public static class OfficialServerDiscoveryService
{
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    internal static readonly Uri CalendarUri = new("https://lobby.legends.travian.com/api/calendar");
    internal static readonly Uri MetadataUri = new("https://lobby.legends.travian.com/api/metadata");

    public static async Task<List<ServerOption>> FetchSpecialServersAsync(
        AccountEntry? account,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        HttpClient http;
        try
        {
            http = CreateClient(account);
        }
        catch (InvalidOperationException)
        {
            var protectedCache = LoadCache(projectRoot);
            if (protectedCache.Count > 0)
            {
                Debug.WriteLine($"[official-server-discovery] Direct discovery blocked; using {protectedCache.Count} cached special gameworld(s).");
                return protectedCache;
            }

            throw;
        }

        using (http)
        {
            var payloads = await Task.WhenAll(
                FetchJsonAsync(http, MetadataUri, cancellationToken),
                FetchJsonAsync(http, CalendarUri, cancellationToken));
            var validPayloads = payloads
                .Where(payload => payload is not null && IsJsonArrayPayload(payload))
                .Select(payload => payload!)
                .ToList();
            var servers = ParseSpecialServers(validPayloads);
            if (servers.Count > 0 && validPayloads.Count < payloads.Length)
            {
                servers = servers
                    .Concat(LoadCache(projectRoot))
                    .GroupBy(server => server.BaseUrl.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                Debug.WriteLine($"[official-server-discovery] Discovery was partial; merged live results with cache ({servers.Count} worlds total).");
            }
            if (servers.Count > 0)
            {
                try
                {
                    SaveCache(projectRoot, servers);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[official-server-discovery] Could not save last-known-good cache: {ex.Message}");
                }
            }
            else
            {
                servers = LoadCache(projectRoot);
                if (servers.Count > 0)
                {
                    Debug.WriteLine($"[official-server-discovery] Live sources yielded no usable worlds; using {servers.Count} cached special gameworld(s).");
                }
            }

            Debug.WriteLine($"[official-server-discovery] Loaded {servers.Count} special gameworld(s).");
            return servers;
        }
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
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Travian server response was not an array.");
                }

                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

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
            catch (JsonException ex)
            {
                Debug.WriteLine($"[official-server-discovery] Ignored malformed discovery payload: {ex.Message}");
            }
        }

        return servers;
    }

    internal static void SaveCache(
        string projectRoot,
        IReadOnlyList<ServerOption> servers,
        DateTimeOffset? savedAtUtc = null)
    {
        var cache = new OfficialServerDiscoveryCache(savedAtUtc ?? DateTimeOffset.UtcNow, servers.ToList());
        AtomicFile.WriteAllText(CachePath(projectRoot), JsonSerializer.Serialize(cache, CacheJsonOptions));
    }

    internal static List<ServerOption> LoadCache(string projectRoot, DateTimeOffset? nowUtc = null)
    {
        var path = CachePath(projectRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var cache = JsonSerializer.Deserialize<OfficialServerDiscoveryCache>(File.ReadAllText(path), CacheJsonOptions);
            if (cache is null || cache.Servers is null)
            {
                return [];
            }

            var age = (nowUtc ?? DateTimeOffset.UtcNow) - cache.SavedAtUtc;
            if (age < TimeSpan.Zero || age > CacheMaxAge)
            {
                return [];
            }

            return cache.Servers
                .Where(server => server is not null
                    && Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && uri.Host.EndsWith(".travian.com", StringComparison.OrdinalIgnoreCase))
                .GroupBy(server => server.BaseUrl.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
                .Select(group => new ServerOption
                {
                    Name = group.First().Name,
                    BaseUrl = group.Key,
                    Group = OfficialServerCatalog.SpecialGroupName,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[official-server-discovery] Could not load cache '{path}': {ex.Message}");
            return [];
        }
    }

    private static string CachePath(string projectRoot)
        => Path.Combine(projectRoot, "config", "cache", "official-special-servers.json");

    private static bool IsJsonArrayPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[official-server-discovery] Ignored malformed discovery payload: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> FetchJsonAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            Debug.WriteLine($"[official-server-discovery] Fetching {uri}");
            using var response = await http.GetAsync(uri, cancellationToken);
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

    internal static ServerDiscoveryProxyRoute ResolveProxyRoute(AccountEntry? account)
    {
        if (account is null || !account.ProxyEnabled)
        {
            if (account?.NeverUseOwnIp == true)
            {
                throw new InvalidOperationException(
                    $"Special server discovery was blocked because account '{account.Name}' forbids direct traffic but its proxy is disabled.");
            }

            return ServerDiscoveryProxyRoute.Direct;
        }

        if (!ProxyParser.TryBuild(account.ProxyServer, out var proxy, out _) || proxy is null
            || !Uri.TryCreate(proxy.Server, UriKind.Absolute, out var proxyUri))
        {
            if (account.NeverUseOwnIp)
            {
                throw new InvalidOperationException(
                    $"Special server discovery was blocked because account '{account.Name}' forbids direct traffic but has no valid proxy.");
            }

            return ServerDiscoveryProxyRoute.Direct;
        }

        return new ServerDiscoveryProxyRoute(proxyUri, proxy.Username, proxy.Password);
    }

    private static HttpClient CreateClient(AccountEntry? account)
    {
        var route = ResolveProxyRoute(account);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = route.UseProxy,
        };
        if (route.UseProxy)
        {
            var webProxy = new WebProxy(route.ProxyUri!);
            if (!string.IsNullOrWhiteSpace(route.Username))
            {
                webProxy.Credentials = new NetworkCredential(route.Username, route.Password ?? string.Empty);
            }

            handler.Proxy = webProxy;
        }

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TbotUltra", "1.0"));
        return client;
    }
}

internal sealed record OfficialServerDiscoveryCache(DateTimeOffset SavedAtUtc, List<ServerOption> Servers);

internal sealed record ServerDiscoveryProxyRoute(Uri? ProxyUri, string? Username, string? Password)
{
    internal static readonly ServerDiscoveryProxyRoute Direct = new(null, null, null);
    internal bool UseProxy => ProxyUri is not null;
}
