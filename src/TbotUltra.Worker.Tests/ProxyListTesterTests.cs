using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ProxyListTesterTests
{
    [Fact]
    public void ParseCandidates_AppliesChosenSchemeToBareLines()
    {
        var candidates = ProxyListTester.ParseCandidates("1.2.3.4:8080\n5.6.7.8:1080", "socks5", 0);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("socks5://1.2.3.4:8080", candidates[0].Server);
        Assert.Equal("socks5", candidates[1].Scheme);
        Assert.Equal("5.6.7.8:1080", candidates[1].HostPort);
    }

    [Fact]
    public void ParseCandidates_SkipsBlanksCommentsAndInvalidLines()
    {
        var text = "1.2.3.4:8080\n\n  \n# comment\nnotaproxy\n5.6.7.8:99999\n9.9.9.9:53";
        var candidates = ProxyListTester.ParseCandidates(text, "http", 0);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("1.2.3.4:8080", candidates[0].HostPort);
        Assert.Equal("9.9.9.9:53", candidates[1].HostPort);
    }

    [Fact]
    public void ParseCandidates_DeduplicatesByHostPort()
    {
        var candidates = ProxyListTester.ParseCandidates("1.2.3.4:8080\n1.2.3.4:8080\n1.2.3.4:9090", "socks5", 0);

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void ParseCandidates_HonoursMaxProxiesLimit()
    {
        var text = "1.1.1.1:1\n2.2.2.2:2\n3.3.3.3:3\n4.4.4.4:4";
        var candidates = ProxyListTester.ParseCandidates(text, "socks5", 2);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("1.1.1.1:1", candidates[0].HostPort);
        Assert.Equal("2.2.2.2:2", candidates[1].HostPort);
    }

    [Fact]
    public void ParseCandidates_HonoursInlineSchemeOverChosen()
    {
        var candidates = ProxyListTester.ParseCandidates("http://1.2.3.4:8080", "socks5", 0);

        Assert.Single(candidates);
        Assert.Equal("http", candidates[0].Scheme);
    }

    [Fact]
    public async Task TestAsync_RanksWorkingProxiesByLatencyAndKeepsTopCount()
    {
        var candidates = ProxyListTester.ParseCandidates("1.1.1.1:1\n2.2.2.2:2\n3.3.3.3:3\n4.4.4.4:4", "socks5", 0);

        // Fake probe: latency decreases with the last IP octet; the ".3" proxy is dead.
        var tester = new ProxyListTester(probe: (server, _) =>
        {
            var latency = server.Contains(":4", StringComparison.Ordinal) ? 10
                : server.Contains(":2", StringComparison.Ordinal) ? 20
                : server.Contains(":1", StringComparison.Ordinal) ? 30
                : 0;
            var success = !server.Contains("3.3.3.3", StringComparison.Ordinal);
            return Task.FromResult(new ProxyProbeResult(success, latency));
        });

        var results = await tester.TestAsync(candidates, maxConcurrency: 2, topCount: 2, progress: null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("4.4.4.4:4", results[0].Candidate.HostPort);
        Assert.Equal(10, results[0].LatencyMs);
        Assert.Equal("2.2.2.2:2", results[1].Candidate.HostPort);
    }

    [Fact]
    public async Task TestAsync_NeverExceedsMaxConcurrency()
    {
        var candidates = ProxyListTester.ParseCandidates(
            string.Join("\n", Enumerable.Range(1, 50).Select(i => $"10.0.0.{i}:80")), "socks5", 0);

        var current = 0;
        var peak = 0;
        var gate = new object();
        var tester = new ProxyListTester(probe: async (_, ct) =>
        {
            lock (gate)
            {
                current++;
                peak = Math.Max(peak, current);
            }

            await Task.Delay(5, ct);
            lock (gate)
            {
                current--;
            }

            return new ProxyProbeResult(true, 1);
        });

        await tester.TestAsync(candidates, maxConcurrency: 5, topCount: 10, progress: null, CancellationToken.None);

        Assert.True(peak <= 5, $"Peak concurrency {peak} exceeded the limit of 5.");
    }

    [Fact]
    public async Task TestAsync_ReportsProgressForEveryCandidate()
    {
        var candidates = ProxyListTester.ParseCandidates("1.1.1.1:1\n2.2.2.2:2\n3.3.3.3:3", "socks5", 0);
        var ticks = new List<ProxyTestProgress>();
        var progress = new Progress<ProxyTestProgress>(p =>
        {
            lock (ticks)
            {
                ticks.Add(p);
            }
        });
        var tester = new ProxyListTester(probe: (_, _) => Task.FromResult(new ProxyProbeResult(true, 1)));

        await tester.TestAsync(candidates, maxConcurrency: 1, topCount: 10, progress, CancellationToken.None);

        // Progress<T> marshals via the captured context; give queued callbacks a moment to run.
        await Task.Delay(50);
        lock (ticks)
        {
            Assert.Equal(3, ticks.Max(t => t.Tested));
            Assert.All(ticks, t => Assert.Equal(3, t.Total));
        }
    }

    [Fact]
    public async Task TestAsync_ReturnsEmptyForNoCandidates()
    {
        var tester = new ProxyListTester(probe: (_, _) => Task.FromResult(new ProxyProbeResult(true, 1)));

        var results = await tester.TestAsync(Array.Empty<ProxyCandidate>(), 10, 10, null, CancellationToken.None);

        Assert.Empty(results);
    }
}
