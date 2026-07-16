using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services.Logging;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BrowserActivityStatisticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbot-browser-statistics-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parser_RecordsNavigationAndReloadBySanitizedDestination()
    {
        var snapshot = new BrowserActivityStatisticsSnapshot();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");

        Assert.True(BrowserActivityStatisticsParser.TryApply(
            "[nav] GOTO done target='/dorf1.php' current='https://ts1.example/dorf1.php?newdid=123&token=secret' pages=1",
            snapshot,
            now));
        Assert.True(BrowserActivityStatisticsParser.TryApply(
            "[nav] RELOAD done target='/dorf2.php' current='https://ts1.example/dorf2.php' pages=1",
            snapshot,
            now));

        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.Navigations));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.Reloads));
        Assert.Equal(1, snapshot.Destinations["dorf1 — Resources"].Navigations);
        Assert.Equal(1, snapshot.Destinations["dorf2 — Village center"].Reloads);
        Assert.DoesNotContain(snapshot.Destinations.Keys, key => key.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(now, snapshot.FirstRecordedUtc);
        Assert.Equal(now, snapshot.LastRecordedUtc);
    }

    [Fact]
    public void Parser_RecordsAlwaysOnOperationalMetrics()
    {
        var snapshot = new BrowserActivityStatisticsSnapshot();
        var now = DateTimeOffset.UtcNow;

        BrowserActivityStatisticsParser.TryApply("[keep-alive] refreshing current page to avoid a stale session.", snapshot, now);
        BrowserActivityStatisticsParser.TryApply("[retry:verbose] navigate to dorf1.php failed on attempt 1/3. Retrying...", snapshot, now);
        BrowserActivityStatisticsParser.TryApply("[nav] RELOAD timeout recovered: expected page is usable despite missing navigation event attempt=1/3", snapshot, now);
        BrowserActivityStatisticsParser.TryApply("[browser] main page created pages=1 url='about:blank'", snapshot, now);

        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.KeepAliveRefreshes));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.NavigationRetries));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.ReloadTimeoutRecoveries));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.BrowserPagesOpened));
    }

    [Fact]
    public void Parser_RecordsDetailedTraceMetricsWithoutCountingTraceNavigationAsGoto()
    {
        var snapshot = new BrowserActivityStatisticsSnapshot();
        var now = DateTimeOffset.UtcNow;
        var lines = new[]
        {
            Trace("NAV_OBSERVED", "frame-navigated", "observed", 0),
            Trace("ACTION_END", "dom-click", "observed", 15),
            Trace("INPUT", "dom-change", "observed", 0),
            Trace("READ_END", "resources", "success", 25),
            Trace("WAIT_END", "page-ready", "success", 1250),
            Trace("CACHE", "villages-hit", "hit", 0),
            Trace("CACHE", "villages-miss", "miss", 0),
            Trace("CACHE", "villages-invalidate", "observed", 0),
            Trace("RETRY", "navigation", "retry", 0),
            Trace("REFRESH_END", "village-status", "success", 200),
            Trace("PAGE_CONTEXT", "page-attached", "observed", 0),
            Trace("ERROR", "page-error", "failed", 0),
            Trace("FLOW_END", "task-handler", "deferred", 300),
        };

        foreach (var line in lines)
        {
            Assert.True(BrowserActivityStatisticsParser.TryApply(line, snapshot, now));
        }

        Assert.Equal(0, snapshot.Metric(BrowserActivityStatisticsParser.Navigations));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.ObservedTransitions));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.BrowserActions));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.InputOperations));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.ReadOperations));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.WaitOperations));
        Assert.Equal(1250, snapshot.Metric(BrowserActivityStatisticsParser.WaitMilliseconds));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.CacheHits));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.CacheMisses));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.CacheInvalidations));
        Assert.Equal(1, snapshot.Metric(BrowserActivityStatisticsParser.FlowsDeferred));
    }

    [Fact]
    public void Store_PersistsPerAccountAndClearRemovesLifetimeCounters()
    {
        var alice = new BrowserActivityStatisticsSnapshot();
        BrowserActivityStatisticsParser.TryApply(
            "[nav] GOTO done target='/dorf1.php' current='https://example.com/dorf1.php' pages=1",
            alice,
            DateTimeOffset.UtcNow);

        BrowserActivityStatisticsStore.Save(_root, "alice", alice);

        Assert.Equal(1, BrowserActivityStatisticsStore.Load(_root, "alice").Metric(BrowserActivityStatisticsParser.Navigations));
        Assert.Equal(0, BrowserActivityStatisticsStore.Load(_root, "bob").Metric(BrowserActivityStatisticsParser.Navigations));
        Assert.True(File.Exists(AccountStoragePaths.BrowserActivityStatisticsPath(_root, "alice")));

        BrowserActivityStatisticsStore.Clear(_root, "alice");

        Assert.Equal(0, BrowserActivityStatisticsStore.Load(_root, "alice").Metric(BrowserActivityStatisticsParser.Navigations));
    }

    [Fact]
    public void Store_QuarantinesCorruptLifetimeFile()
    {
        var path = AccountStoragePaths.BrowserActivityStatisticsPath(_root, "alice");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ invalid json");

        var result = BrowserActivityStatisticsStore.Load(_root, "alice");

        Assert.Equal(0, result.Metric(BrowserActivityStatisticsParser.Navigations));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "browser_activity_statistics.json.corrupt-*"));
    }

    private static string Trace(string eventName, string action, string result, long durationMs)
        => $"[browser-trace:verbose] run=abc seq=1 task='task' account='account' village='03' event={eventName} action='{action}' result={result} elapsedMs=10 durationMs={durationMs} url='https://example.com/dorf1.php' detail='-'";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
