using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class CapitalProfileCoordinateSourceTests
{
    [Fact]
    public void CapitalProfileCheck_PrefersSignedCoordinateLinkAndStripsBidiControls()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var fixture = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker.Tests",
            "Fixtures",
            "player_profile_capital.html"));
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Core",
            "TravianClient.CapitalCache.cs"));
        var checkMethod = source[
            source.IndexOf("public async Task<CapitalProfileCheckResult> CheckCapitalFromProfileAsync", StringComparison.Ordinal)..
            source.IndexOf("public async Task SetVerifiedCapitalStateAsync", StringComparison.Ordinal)];
        var profileTableWait = checkMethod.IndexOf(
            "#playerProfile table.villages tbody tr",
            StringComparison.Ordinal);
        var profileParse = checkMethod.IndexOf(
            "EvaluateAsync<PlayerProfileVillageRowJs[]>",
            StringComparison.Ordinal);

        Assert.Contains("href=\"/karte.php?x=12&amp;y=-34\"", fixture, StringComparison.Ordinal);
        Assert.Contains("class=\"coordinateY\">\u202d−\u202d34", fixture, StringComparison.Ordinal);
        Assert.True(profileTableWait >= 0 && profileParse > profileTableWait);
        Assert.Contains("const coordHref =", checkMethod, StringComparison.Ordinal);
        Assert.Contains("parseCoordinate(coordHref, 'y')", checkMethod, StringComparison.Ordinal);
        Assert.Contains("""[\u200e\u200f\u202a-\u202e\u2066-\u2069]""", checkMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[capital:verbose] profile check incomplete:", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("[capital] profile check failed:", checkMethod, StringComparison.Ordinal);
    }
}
