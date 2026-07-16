using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services.Logging;

internal sealed class BrowserDestinationCounters
{
    public long Navigations { get; set; }
    public long Reloads { get; set; }
}

internal sealed class BrowserActivityStatisticsSnapshot
{
    public DateTimeOffset? FirstRecordedUtc { get; set; }
    public DateTimeOffset? LastRecordedUtc { get; set; }
    public Dictionary<string, long> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, BrowserDestinationCounters> Destinations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long Metric(string key)
        => Metrics.TryGetValue(key, out var value) ? Math.Max(0, value) : 0;
}

internal static partial class BrowserActivityStatisticsParser
{
    public const string Navigations = "Navigations";
    public const string Reloads = "Reloads";
    public const string KeepAliveRefreshes = "Keep-alive refreshes";
    public const string NavigationRetries = "Navigation retries";
    public const string ReloadTimeoutRecoveries = "Reload timeout recoveries";
    public const string BrowserPagesOpened = "Browser pages opened";
    public const string ObservedTransitions = "Observed page transitions (trace)";
    public const string RefreshOperations = "Refresh operations (trace)";
    public const string BrowserActions = "Browser actions (trace)";
    public const string InputOperations = "Input operations (trace)";
    public const string ReadOperations = "Read operations (trace)";
    public const string CacheHits = "Cache hits (trace)";
    public const string CacheMisses = "Cache misses (trace)";
    public const string CacheInvalidations = "Cache invalidations (trace)";
    public const string WaitOperations = "Wait operations (trace)";
    public const string WaitMilliseconds = "Wait milliseconds (trace)";
    public const string RetryOperations = "Retry operations (trace)";
    public const string PageContextEvents = "Page/context events (trace)";
    public const string TraceErrors = "Browser errors (trace)";
    public const string FlowsSucceeded = "Flows succeeded (trace)";
    public const string FlowsDeferred = "Flows deferred (trace)";
    public const string FlowsCanceled = "Flows canceled (trace)";
    public const string FlowsBlocked = "Flows blocked (trace)";
    public const string FlowsFailed = "Flows failed (trace)";

    public static readonly string[] MetricOrder =
    [
        Navigations,
        Reloads,
        KeepAliveRefreshes,
        NavigationRetries,
        ReloadTimeoutRecoveries,
        BrowserPagesOpened,
        ObservedTransitions,
        RefreshOperations,
        BrowserActions,
        InputOperations,
        ReadOperations,
        CacheHits,
        CacheMisses,
        CacheInvalidations,
        WaitOperations,
        WaitMilliseconds,
        RetryOperations,
        PageContextEvents,
        TraceErrors,
        FlowsSucceeded,
        FlowsDeferred,
        FlowsCanceled,
        FlowsBlocked,
        FlowsFailed,
    ];

    public static bool TryApply(string line, BrowserActivityStatisticsSnapshot snapshot, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var gotoMatch = GotoDoneRegex().Match(line);
        if (gotoMatch.Success)
        {
            Increment(snapshot, Navigations);
            IncrementDestination(snapshot, gotoMatch.Groups[1].Value, isReload: false);
            Touch(snapshot, nowUtc);
            return true;
        }

        var reloadMatch = ReloadDoneRegex().Match(line);
        if (reloadMatch.Success)
        {
            Increment(snapshot, Reloads);
            IncrementDestination(snapshot, reloadMatch.Groups[1].Value, isReload: true);
            Touch(snapshot, nowUtc);
            return true;
        }

        if (line.Contains("[keep-alive] refreshing current page", StringComparison.OrdinalIgnoreCase))
        {
            Increment(snapshot, KeepAliveRefreshes);
            Touch(snapshot, nowUtc);
            return true;
        }

        if (line.Contains("[nav] RELOAD timeout recovered", StringComparison.OrdinalIgnoreCase))
        {
            Increment(snapshot, ReloadTimeoutRecoveries);
            Touch(snapshot, nowUtc);
            return true;
        }

        if ((line.Contains("[retry:verbose] navigate to ", StringComparison.OrdinalIgnoreCase)
             || line.Contains("[nav] RELOAD transient timeout", StringComparison.OrdinalIgnoreCase))
            && line.Contains("attempt", StringComparison.OrdinalIgnoreCase))
        {
            Increment(snapshot, NavigationRetries);
            Touch(snapshot, nowUtc);
            return true;
        }

        if (line.Contains("[browser] main page created", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[browser] isolated external page created", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[browser-video] isolated bonus-video browser opened", StringComparison.OrdinalIgnoreCase))
        {
            Increment(snapshot, BrowserPagesOpened);
            Touch(snapshot, nowUtc);
            return true;
        }

        if (!line.Contains("[browser-trace:verbose]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trace = TraceEventRegex().Match(line);
        if (!trace.Success)
        {
            return false;
        }

        var eventName = trace.Groups[1].Value;
        var action = trace.Groups[2].Value;
        var result = trace.Groups[3].Value;
        var durationMs = long.TryParse(trace.Groups[4].Value, out var parsedDuration)
            ? Math.Max(0, parsedDuration)
            : 0;
        var metric = ResolveTraceMetric(eventName, action, result);
        if (metric is null)
        {
            return false;
        }

        Increment(snapshot, metric);
        if (string.Equals(metric, WaitOperations, StringComparison.Ordinal))
        {
            Increment(snapshot, WaitMilliseconds, durationMs);
        }

        Touch(snapshot, nowUtc);
        return true;
    }

    public static string NormalizeDestination(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Unknown";
        }

        if (uri.Host.Contains("lobby", StringComparison.OrdinalIgnoreCase))
        {
            return "Travian lobby";
        }

        if (uri.Host.Contains("travcotools", StringComparison.OrdinalIgnoreCase))
        {
            return "Travco inactive search";
        }

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            return "about:blank — Browser cleanup";
        }

        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return path switch
        {
            "" => "Game world root",
            "/dorf1.php" => "dorf1 — Resources",
            "/dorf2.php" => "dorf2 — Village center",
            "/build.php" => "build.php — Building",
            "/karte.php" => "karte.php — Map",
            "/berichte.php" => "berichte.php — Reports",
            "/messages.php" => "messages.php — Messages",
            "/spieler.php" => "spieler.php — Profile",
            "/statistiken.php" or "/statistics.php" => "Statistics",
            "/tasks" or "/tasks.php" => "Tasks",
            _ when path.Contains("farmlist", StringComparison.Ordinal) => "Farm lists",
            _ when path.Contains("hero", StringComparison.Ordinal) => "Hero",
            _ => string.IsNullOrWhiteSpace(path) ? "Game world root" : path.TrimStart('/'),
        };
    }

    private static string? ResolveTraceMetric(string eventName, string action, string result)
    {
        if (eventName.Equals("NAV_OBSERVED", StringComparison.OrdinalIgnoreCase)) return ObservedTransitions;
        if (eventName.Equals("REFRESH_END", StringComparison.OrdinalIgnoreCase)) return RefreshOperations;
        if (eventName.Equals("ACTION_END", StringComparison.OrdinalIgnoreCase)) return BrowserActions;
        if (eventName.Equals("INPUT", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("INPUT_END", StringComparison.OrdinalIgnoreCase)) return InputOperations;
        if (eventName.Equals("READ_END", StringComparison.OrdinalIgnoreCase)) return ReadOperations;
        if (eventName.Equals("WAIT_END", StringComparison.OrdinalIgnoreCase)) return WaitOperations;
        if (eventName.Equals("RETRY", StringComparison.OrdinalIgnoreCase)) return RetryOperations;
        if (eventName.Equals("PAGE_CONTEXT", StringComparison.OrdinalIgnoreCase)) return PageContextEvents;
        if (eventName.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) return TraceErrors;
        if (eventName.Equals("CACHE", StringComparison.OrdinalIgnoreCase))
        {
            if (action.Contains("hit", StringComparison.OrdinalIgnoreCase) || result.Equals("hit", StringComparison.OrdinalIgnoreCase)) return CacheHits;
            if (action.Contains("miss", StringComparison.OrdinalIgnoreCase) || result.Equals("miss", StringComparison.OrdinalIgnoreCase)) return CacheMisses;
            if (action.Contains("invalidate", StringComparison.OrdinalIgnoreCase)) return CacheInvalidations;
        }

        if (eventName.Equals("FLOW_END", StringComparison.OrdinalIgnoreCase))
        {
            return result.ToLowerInvariant() switch
            {
                "success" => FlowsSucceeded,
                "deferred" => FlowsDeferred,
                "canceled" => FlowsCanceled,
                "blocked" => FlowsBlocked,
                "failed" or "incomplete" => FlowsFailed,
                _ => null,
            };
        }

        return null;
    }

    private static void Increment(BrowserActivityStatisticsSnapshot snapshot, string metric, long amount = 1)
        => snapshot.Metrics[metric] = snapshot.Metric(metric) + Math.Max(0, amount);

    private static void IncrementDestination(BrowserActivityStatisticsSnapshot snapshot, string url, bool isReload)
    {
        var destination = NormalizeDestination(url);
        if (!snapshot.Destinations.TryGetValue(destination, out var counters))
        {
            counters = new BrowserDestinationCounters();
            snapshot.Destinations[destination] = counters;
        }

        if (isReload)
        {
            counters.Reloads++;
        }
        else
        {
            counters.Navigations++;
        }
    }

    private static void Touch(BrowserActivityStatisticsSnapshot snapshot, DateTimeOffset nowUtc)
    {
        snapshot.FirstRecordedUtc ??= nowUtc.ToUniversalTime();
        snapshot.LastRecordedUtc = nowUtc.ToUniversalTime();
    }

    [GeneratedRegex("\\[nav\\]\\s+GOTO done.*?current='([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex GotoDoneRegex();

    [GeneratedRegex("\\[nav\\]\\s+RELOAD done.*?current='([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex ReloadDoneRegex();

    [GeneratedRegex("event=([A-Z_]+) action='([^']*)' result=([^\\s]+).*?durationMs=(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TraceEventRegex();
}

internal static class BrowserActivityStatisticsStore
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static BrowserActivityStatisticsSnapshot Load(string projectRoot, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return new BrowserActivityStatisticsSnapshot();
        }

        lock (FileLock)
        {
            var path = AccountStoragePaths.BrowserActivityStatisticsPath(projectRoot, accountName);
            if (!File.Exists(path))
            {
                return new BrowserActivityStatisticsSnapshot();
            }

            try
            {
                var snapshot = JsonSerializer.Deserialize<BrowserActivityStatisticsSnapshot>(File.ReadAllText(path), JsonOptions)
                               ?? new BrowserActivityStatisticsSnapshot();
                snapshot.Metrics = new Dictionary<string, long>(snapshot.Metrics ?? [], StringComparer.OrdinalIgnoreCase);
                snapshot.Destinations = new Dictionary<string, BrowserDestinationCounters>(
                    (snapshot.Destinations ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.Key) && item.Value is not null),
                    StringComparer.OrdinalIgnoreCase);
                return snapshot;
            }
            catch
            {
                try
                {
                    File.Move(
                        path,
                        $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                        overwrite: true);
                }
                catch
                {
                    // Preserve the unreadable file if quarantine itself is blocked.
                }

                return new BrowserActivityStatisticsSnapshot();
            }
        }
    }

    public static void Save(string projectRoot, string? accountName, BrowserActivityStatisticsSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        lock (FileLock)
        {
            var path = AccountStoragePaths.BrowserActivityStatisticsPath(projectRoot, accountName);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
    }

    public static void Clear(string projectRoot, string? accountName)
        => Save(projectRoot, accountName, new BrowserActivityStatisticsSnapshot());
}
