using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class OfficialServerDiscoveryServiceTests
{
    [Fact]
    public void ParseSpecialServers_IncludesNonNormalAndNonStandardWorldsOnly()
    {
        const string json = """
        [
          { "metadata": { "name": "Europe 6", "type": "normal", "url": "https://ts6.x1.europe.travian.com/" } },
          { "metadata": { "name": "Travian Schild", "type": "LocalServer", "url": "https://schild.x3.netherlands.travian.com/" } },
          { "metadata": { "name": "Community Week x5", "type": "CW", "url": "https://cw.x5.international.travian.com/" } },
          { "metadata": { "name": "Unclassified Event", "type": "normal", "url": "https://event.x2.america.travian.com/" } },
          { "metadata": { "name": "Not published", "type": "special" } },
          { "metadata": { "name": "Foreign", "type": "special", "url": "https://example.com/" } }
        ]
        """;

        var servers = OfficialServerDiscoveryService.ParseSpecialServers(json);

        Assert.Equal(3, servers.Count);
        Assert.Contains(servers, item => item.Name == "Travian Schild" && item.BaseUrl == "https://schild.x3.netherlands.travian.com");
        Assert.Contains(servers, item => item.Name == "Community Week x5");
        Assert.Contains(servers, item => item.Name == "Unclassified Event");
        Assert.All(servers, item => Assert.Equal(OfficialServerCatalog.SpecialGroupName, item.Group));
    }

    [Fact]
    public void BuildPickerServers_PutsSpecialFirstAndHidesMatchingCustomDuplicate()
    {
        var special = new ServerOption { Name = "Travian Schild", BaseUrl = "https://schild.x3.netherlands.travian.com", Group = "Special" };
        var duplicateCustom = new ServerOption { Name = "My Schild", BaseUrl = "https://schild.x3.netherlands.travian.com/" };
        var custom = new ServerOption { Name = "SS-Travi", BaseUrl = "https://example.ss-travi.com" };
        var official = new ServerOption { Name = "Europe 1", BaseUrl = "https://ts1.x1.europe.travian.com", Group = "Europe" };

        var result = OfficialServerCatalog.BuildPickerServers([duplicateCustom, custom], [special], [official]);

        Assert.Equal(new[] { special, custom, official }, result);
        Assert.Equal("Special", result[0].Group);
        Assert.Equal("Custom", result[1].Group);
        Assert.Equal("Europe", result[2].Group);
    }

    [Fact]
    public void ParseSpecialServers_MergesActiveAndUpcomingWorldsAndSkipsEndedEntries()
    {
        const string activeJson = """
        [
          { "end": 300, "metadata": { "name": "New Year's Special", "type": "NYS", "url": "https://nys.x1.america.travian.com/" } },
          { "end": 100, "metadata": { "name": "Ended Event", "type": "special", "url": "https://ended.x1.europe.travian.com/" } }
        ]
        """;
        const string upcomingJson = """
        [
          { "metadata": { "name": "Duplicate Calendar Entry", "type": "NYS", "url": "https://nys.x1.america.travian.com/" } },
          { "metadata": { "name": "Community Week", "type": "CW", "url": "https://cw.x2.international.travian.com/" } }
        ]
        """;

        var servers = OfficialServerDiscoveryService.ParseSpecialServers(
            [activeJson, upcomingJson],
            DateTimeOffset.FromUnixTimeSeconds(200));

        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, item => item.Name == "New Year's Special");
        Assert.Contains(servers, item => item.Name == "Community Week");
        Assert.DoesNotContain(servers, item => item.Name == "Ended Event");
    }

    [Fact]
    public void ParseSpecialServers_IgnoresMalformedSourceAndKeepsValidSource()
    {
        const string validJson = """
        [
          { "metadata": { "name": "Community Week", "type": "CW", "url": "https://cw.x2.international.travian.com/" } }
        ]
        """;

        var servers = OfficialServerDiscoveryService.ParseSpecialServers(["{broken", validJson]);

        Assert.Single(servers);
        Assert.Equal("Community Week", servers[0].Name);
    }

    [Fact]
    public void Cache_RoundTripsRecentListAndRejectsExpiredOrMalformedData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-discovery-{Guid.NewGuid():N}");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var server = new ServerOption
            {
                Name = "Travian Schild",
                BaseUrl = "https://schild.x3.netherlands.travian.com",
                Group = "Special",
            };
            OfficialServerDiscoveryService.SaveCache(root, [server], now);

            Assert.Single(OfficialServerDiscoveryService.LoadCache(root, now.AddDays(6)));
            Assert.Empty(OfficialServerDiscoveryService.LoadCache(root, now.AddDays(8)));

            var path = Path.Combine(root, "config", "cache", "official-special-servers.json");
            File.WriteAllText(path, "{broken");
            Assert.Empty(OfficialServerDiscoveryService.LoadCache(root, now));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveProxyRoute_UsesConfiguredProxy()
    {
        var route = OfficialServerDiscoveryService.ResolveProxyRoute(new ServerOptionAccountBuilder
        {
            ProxyEnabled = true,
            ProxyServer = "http://user:secret@proxy.example:8080",
            NeverUseOwnIp = true,
        }.Build());

        Assert.True(route.UseProxy);
        Assert.Equal("proxy.example", route.ProxyUri?.Host);
        Assert.Equal("user", route.Username);
        Assert.Equal("secret", route.Password);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "not-a-proxy")]
    public void ResolveProxyRoute_BlocksDirectTrafficWhenNeverUseOwnIp(bool proxyEnabled, string proxyServer)
    {
        var account = new ServerOptionAccountBuilder
        {
            ProxyEnabled = proxyEnabled,
            ProxyServer = proxyServer,
            NeverUseOwnIp = true,
        }.Build();

        Assert.Throws<InvalidOperationException>(() => OfficialServerDiscoveryService.ResolveProxyRoute(account));
    }

    private sealed class ServerOptionAccountBuilder
    {
        internal bool ProxyEnabled { get; init; }
        internal string ProxyServer { get; init; } = string.Empty;
        internal bool NeverUseOwnIp { get; init; }

        internal AccountEntry Build() => new()
        {
            Name = "account",
            ProxyEnabled = ProxyEnabled,
            ProxyServer = ProxyServer,
            NeverUseOwnIp = NeverUseOwnIp,
        };
    }
}
