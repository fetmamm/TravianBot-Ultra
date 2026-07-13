using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProxyUsageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tbot-ultra-proxy-usage-tests",
        Guid.NewGuid().ToString("N"));

    public ProxyUsageStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void RecordUsage_AccumulatesTimeAndCountsOnlyNewSessions()
    {
        var start = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var identity = new ProxyUsageIdentity(
            "proxy:socks5:1.2.3.4:1080",
            "Proxy",
            "1.2.3.4:1080",
            "1.2.3.4",
            "Sweden");

        ProxyUsageStore.RecordUsage(_root, "alice", identity, start, start.AddMinutes(30), startsNewSession: true);
        ProxyUsageStore.RecordUsage(_root, "alice", identity, start.AddMinutes(30), start.AddHours(1), startsNewSession: false);

        var record = Assert.Single(ProxyUsageStore.Load(_root, "alice"));
        Assert.Equal(3600, record.TotalSeconds, precision: 3);
        Assert.Equal(1, record.SessionCount);
        Assert.Equal(start, record.FirstUsedUtc);
        Assert.Equal(start.AddHours(1), record.LastUsedUtc);
    }

    [Fact]
    public void RecordUsage_SeparatesAccountsAndConnections()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var proxy = new ProxyUsageIdentity("proxy:http:proxy.test:8080", "Proxy", "proxy.test:8080", "9.8.7.6", "Germany");
        var direct = new ProxyUsageIdentity("direct:5.6.7.8", "Direct", string.Empty, "5.6.7.8", "Sweden");

        ProxyUsageStore.RecordUsage(_root, "alice", proxy, start, start.AddMinutes(20), startsNewSession: true);
        ProxyUsageStore.RecordUsage(_root, "alice", direct, start.AddMinutes(20), start.AddMinutes(40), startsNewSession: true);
        ProxyUsageStore.RecordUsage(_root, "bob", proxy, start, start.AddMinutes(10), startsNewSession: true);

        Assert.Equal(2, ProxyUsageStore.Load(_root, "alice").Count);
        Assert.Single(ProxyUsageStore.Load(_root, "bob"));
    }

    [Fact]
    public void RecordUsage_RefreshesKnownExitMetadata()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var pending = new ProxyUsageIdentity("proxy:socks5:host.test:1080", "Proxy", "host.test:1080", string.Empty, string.Empty);
        var enriched = pending with { ExitIp = "4.3.2.1", Country = "France" };

        ProxyUsageStore.RecordUsage(_root, "alice", pending, start, start.AddMinutes(5), startsNewSession: true);
        ProxyUsageStore.RecordUsage(_root, "alice", enriched, start.AddMinutes(5), start.AddMinutes(10), startsNewSession: false);

        var record = Assert.Single(ProxyUsageStore.Load(_root, "alice"));
        Assert.Equal("4.3.2.1", record.ExitIp);
        Assert.Equal("France", record.Country);
    }

    [Fact]
    public void Load_CorruptFileReturnsEmptyHistory()
    {
        var path = AccountStoragePaths.ProxyUsagePath(_root, "alice");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ invalid json");

        Assert.Empty(ProxyUsageStore.Load(_root, "alice"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
