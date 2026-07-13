using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProxyFailoverServiceTests
{
    [Fact]
    public async Task FindRecovery_UsesWorkingProxyAssignedToCurrentAccount()
    {
        var account = CreateAccount("socks5://1.1.1.1:1080");
        var current = CreateProxy("current", "1.1.1.1", assignedAccount: "alice");
        var replacement = CreateProxy("replacement", "2.2.2.2", assignedAccount: "alice", latencyMs: 50);
        var otherAccount = CreateProxy("other", "3.3.3.3", assignedAccount: "bob", latencyMs: 10);
        var tester = new ProxyListTester(
            probe: (server, _, _) => Task.FromResult(new ProxyProbeResult(server.Contains("2.2.2.2"), 25)),
            directProbe: (_, _) => Task.FromResult(false));
        var service = new ProxyFailoverService(tester);
        var library = new List<ProxyLibraryEntry> { current, replacement, otherAccount };

        var result = await service.FindRecoveryAsync(
            account,
            library,
            account.ServerUrl,
            _ => { },
            CancellationToken.None);

        Assert.Equal(ProxyFailoverKind.ReplacementProxy, result.Kind);
        Assert.Same(replacement, result.Proxy);
        Assert.False(current.IsWorking);
        Assert.NotNull(current.LastFailureUtc);
        Assert.True(replacement.IsWorking);
        Assert.Contains("alice", replacement.UsedByAccounts);
    }

    [Fact]
    public async Task FindRecovery_FallsBackToDirectOnlyWhenOwnIpIsAllowed()
    {
        var account = CreateAccount("socks5://1.1.1.1:1080");
        var tester = new ProxyListTester(
            probe: (_, _, _) => Task.FromResult(new ProxyProbeResult(false, 0)),
            directProbe: (_, _) => Task.FromResult(true));
        var service = new ProxyFailoverService(tester);

        var result = await service.FindRecoveryAsync(
            account,
            [],
            account.ServerUrl,
            _ => { },
            CancellationToken.None);

        Assert.Equal(ProxyFailoverKind.DirectConnection, result.Kind);
    }

    [Fact]
    public async Task FindRecovery_NeverUsesDirectWhenOwnIpIsBlocked()
    {
        var directProbeCalled = false;
        var account = CreateAccount("socks5://1.1.1.1:1080");
        account.NeverUseOwnIp = true;
        var tester = new ProxyListTester(
            probe: (_, _, _) => Task.FromResult(new ProxyProbeResult(false, 0)),
            directProbe: (_, _) =>
            {
                directProbeCalled = true;
                return Task.FromResult(true);
            });
        var service = new ProxyFailoverService(tester);

        var result = await service.FindRecoveryAsync(
            account,
            [],
            account.ServerUrl,
            _ => { },
            CancellationToken.None);

        Assert.Equal(ProxyFailoverKind.Unavailable, result.Kind);
        Assert.False(directProbeCalled);
    }

    [Fact]
    public void SelectCandidates_SkipsOtherAccountsAndRecentFailures()
    {
        var account = CreateAccount("socks5://1.1.1.1:1080");
        var eligible = CreateProxy("eligible", "2.2.2.2", assignedAccount: "alice");
        var other = CreateProxy("other", "3.3.3.3", assignedAccount: "bob");
        var coolingDown = CreateProxy("cooldown", "4.4.4.4", assignedAccount: "alice");
        coolingDown.LastFailureUtc = DateTime.UtcNow.AddMinutes(-5);

        var result = ProxyFailoverService.SelectCandidates(
            [eligible, other, coolingDown],
            account,
            DateTime.UtcNow);

        Assert.Equal([eligible], result);
    }

    [Fact]
    public void SelectCandidates_RestrictsAndOrdersConfiguredAccountProxies()
    {
        var account = CreateAccount("socks5://1.1.1.1:1080");
        var first = CreateProxy("first", "2.2.2.2", assignedAccount: "alice");
        var second = CreateProxy("second", "3.3.3.3", assignedAccount: "alice");
        var unselected = CreateProxy("unselected", "4.4.4.4", assignedAccount: "alice");

        var result = ProxyFailoverService.SelectCandidates(
            [first, second, unselected],
            account,
            DateTime.UtcNow,
            [second.Id, first.Id]);

        Assert.Equal([second, first], result);
    }

    [Fact]
    public async Task FindRecovery_DoesNotMarkStableProxiesFailedWhenNoRouteCanReachTravian()
    {
        var account = CreateAccount("socks5://1.1.1.1:1080");
        var current = CreateProxy("current", "1.1.1.1", assignedAccount: "alice");
        var replacement = CreateProxy("replacement", "2.2.2.2", assignedAccount: "alice");
        var tester = new ProxyListTester(
            probe: (_, url, _) => Task.FromResult(new ProxyProbeResult(
                url.Contains("gstatic", StringComparison.Ordinal),
                20)),
            directProbe: (_, _) => Task.FromResult(false));
        var service = new ProxyFailoverService(tester);

        var result = await service.FindRecoveryAsync(
            account,
            [current, replacement],
            account.ServerUrl,
            _ => { },
            CancellationToken.None);

        Assert.Equal(ProxyFailoverKind.RetryLater, result.Kind);
        Assert.Null(current.IsWorking);
        Assert.Null(current.LastFailureUtc);
        Assert.Null(replacement.IsWorking);
        Assert.Null(replacement.LastFailureUtc);
    }

    [Fact]
    public async Task FindRecovery_RetriesLaterWhenAllAllowedRoutesAreTemporarilyUnavailable()
    {
        var account = CreateAccount("socks5://1.1.1.1:1080");
        var tester = new ProxyListTester(
            probe: (_, _, _) => Task.FromResult(new ProxyProbeResult(false, 0)),
            directProbe: (_, _) => Task.FromResult(false));
        var service = new ProxyFailoverService(tester);

        var result = await service.FindRecoveryAsync(
            account,
            [],
            account.ServerUrl,
            _ => { },
            CancellationToken.None);

        Assert.Equal(ProxyFailoverKind.RetryLater, result.Kind);
    }

    private static AccountEntry CreateAccount(string proxyServer) => new()
    {
        Name = "alice",
        ServerUrl = "https://travian.example/",
        ProxyEnabled = true,
        ProxyServer = proxyServer,
    };

    private static ProxyLibraryEntry CreateProxy(
        string id,
        string host,
        string? assignedAccount,
        long? latencyMs = null) => new()
    {
        Id = id,
        Name = id,
        Scheme = "socks5",
        Host = host,
        Port = 1080,
        AssignedAccount = assignedAccount,
        LatencyMs = latencyMs,
        CreatedAtUtc = DateTime.UtcNow,
    };
}
