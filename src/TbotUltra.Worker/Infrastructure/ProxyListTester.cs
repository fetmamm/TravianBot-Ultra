using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TbotUltra.Worker.Infrastructure;

/// <summary>A single proxy to test: chosen scheme plus host/port parsed from one pasted line.</summary>
public sealed record ProxyCandidate(string Scheme, string Host, int Port)
{
    /// <summary>Full connection string, e.g. <c>socks5://1.2.3.4:1080</c>.</summary>
    public string Server => $"{Scheme}://{Host}:{Port}";

    /// <summary>Display form without scheme, e.g. <c>1.2.3.4:1080</c>.</summary>
    public string HostPort => $"{Host}:{Port}";
}

/// <summary>Outcome of a single lightweight probe: whether the proxy answered and how fast.</summary>
public sealed record ProxyProbeResult(bool Success, long LatencyMs);

/// <summary>Progress tick raised while a proxy list is being tested.</summary>
public sealed record ProxyTestProgress(int Tested, int Total, int Found);

/// <summary>A working proxy kept after testing, with its measured latency.</summary>
public sealed record ProxyTestResult(ProxyCandidate Candidate, long LatencyMs);

/// <summary>Best-effort IP/country lookup for a proxy (used only to enrich the top results).</summary>
public sealed record ProxyEnrichment(string Ip, string Country);

/// <summary>
/// Tests a pasted list of proxies (potentially thousands) with lightweight <see cref="HttpClient"/>
/// requests instead of a full browser, so it stays fast and low on resources. Parsing, ranking and
/// concurrency limiting are pure and unit-testable; the actual network probe is injectable so tests
/// never touch the network. Ranks survivors by latency and returns the fastest few.
/// </summary>
public sealed class ProxyListTester
{
    // HTTPS target so the probe exercises the same CONNECT tunnelling the bot's browser needs, which
    // predicts real usefulness better than a plain-HTTP relay check. 204 = tiny, no body to download.
    private const string LatencyProbeUrl = "https://www.gstatic.com/generate_204";
    private const string IpLookupUrl = "https://ipwho.is/";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    private readonly Func<string, CancellationToken, Task<ProxyProbeResult>> _probe;
    private readonly Action<string>? _log;

    /// <param name="probe">
    /// Override the network probe (mainly for tests). Receives a <see cref="ProxyCandidate.Server"/>
    /// string and returns success + latency. Defaults to a real HttpClient-through-proxy probe.
    /// </param>
    /// <param name="log">Optional log sink for diagnostics.</param>
    public ProxyListTester(
        Func<string, CancellationToken, Task<ProxyProbeResult>>? probe = null,
        Action<string>? log = null)
    {
        _probe = probe ?? DefaultProbeAsync;
        _log = log;
    }

    /// <summary>
    /// Parses pasted text (one <c>host:port</c> per line) into unique candidates using the chosen
    /// scheme. Blank lines, comment lines (<c>#</c>) and malformed rows are skipped; duplicates are
    /// dropped; at most <paramref name="maxProxies"/> are kept (0 or negative = no limit).
    /// </summary>
    public static IReadOnlyList<ProxyCandidate> ParseCandidates(string? pastedText, string scheme, int maxProxies)
    {
        var result = new List<ProxyCandidate>();
        if (string.IsNullOrWhiteSpace(pastedText))
        {
            return result;
        }

        var normalizedScheme = NormalizeScheme(scheme);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limit = maxProxies <= 0 ? int.MaxValue : maxProxies;

        foreach (var rawLine in pastedText.Split('\n'))
        {
            if (result.Count >= limit)
            {
                break;
            }

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseLine(line, normalizedScheme, out var candidate) || candidate is null)
            {
                continue;
            }

            if (seen.Add(candidate.HostPort))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// Tests every candidate with at most <paramref name="maxConcurrency"/> probes in flight, reports
    /// progress, and returns the <paramref name="topCount"/> fastest working proxies (0 = keep all).
    /// On cancellation it returns whatever passed before the cancel.
    /// </summary>
    public async Task<IReadOnlyList<ProxyTestResult>> TestAsync(
        IReadOnlyList<ProxyCandidate> candidates,
        int maxConcurrency,
        int topCount,
        IProgress<ProxyTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<ProxyTestResult>();
        }

        _log?.Invoke($"[proxytest] starting: {candidates.Count} proxies, {maxConcurrency} parallel, top {topCount}.");
        var working = new ConcurrentBag<ProxyTestResult>();
        var tested = 0;
        var found = 0;
        using var throttle = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = candidates.Select(async candidate =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var probe = await _probe(candidate.Server, cancellationToken).ConfigureAwait(false);
                if (probe.Success)
                {
                    working.Add(new ProxyTestResult(candidate, probe.LatencyMs));
                    Interlocked.Increment(ref found);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A dead proxy is the normal case, not an error worth failing the whole run over.
                _log?.Invoke($"[proxytest] probe error for {candidate.HostPort}: {ex.Message}");
            }
            finally
            {
                var doneCount = Interlocked.Increment(ref tested);
                progress?.Report(new ProxyTestProgress(doneCount, candidates.Count, Volatile.Read(ref found)));
                throttle.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("[proxytest] cancelled; returning proxies tested so far.");
        }

        var ranked = working
            .OrderBy(item => item.LatencyMs)
            .Take(topCount <= 0 ? working.Count : topCount)
            .ToList();
        _log?.Invoke($"[proxytest] done: {working.Count} working, returning top {ranked.Count}.");
        return ranked;
    }

    /// <summary>
    /// Best-effort IP/country lookup through the proxy, used only to enrich the handful of top
    /// results. Never throws for a bad proxy — returns empty fields instead.
    /// </summary>
    public async Task<ProxyEnrichment> EnrichAsync(string server, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri(server)),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = ProbeTimeout,
        };
        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = ProbeTimeout };

        try
        {
            var json = await client.GetStringAsync(IpLookupUrl, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var ip = ReadJsonString(root, "ip");
            var country = ReadJsonString(root, "country");
            return new ProxyEnrichment(ip, country);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[proxytest] enrich failed for {ProxyParser.MaskForLog(server)}: {ex.Message}");
            return new ProxyEnrichment(string.Empty, string.Empty);
        }
    }

    private static async Task<ProxyProbeResult> DefaultProbeAsync(string server, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri(server)),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = ProbeTimeout,
        };
        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = ProbeTimeout };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Any HTTP response means the proxy relayed the request, so it is reachable/usable.
            using var response = await client.GetAsync(
                LatencyProbeUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new ProxyProbeResult(true, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeout, refused connection, TLS failure, etc. — the proxy is not usable.
            return new ProxyProbeResult(false, 0);
        }
    }

    private static bool TryParseLine(string line, string scheme, out ProxyCandidate? candidate)
    {
        candidate = null;

        var effectiveScheme = scheme;
        var rest = line;
        var schemeIndex = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            // Honour an inline scheme if the pasted line already has one.
            effectiveScheme = NormalizeScheme(line[..schemeIndex]);
            rest = line[(schemeIndex + 3)..];
        }

        // Drop any credentials (unusual for public lists) — keep host:port only.
        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            rest = rest[(atIndex + 1)..];
        }

        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex <= 0 || colonIndex >= rest.Length - 1)
        {
            return false;
        }

        var host = rest[..colonIndex].Trim();
        var portText = rest[(colonIndex + 1)..].Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        candidate = new ProxyCandidate(effectiveScheme, host, port);
        return true;
    }

    private static string NormalizeScheme(string? scheme)
    {
        var value = (scheme ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "socks4" => "socks4",
            "socks4a" => "socks4a",
            "http" => "http",
            "https" => "http",
            _ => "socks5",
        };
    }

    private static string ReadJsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
    }
}
