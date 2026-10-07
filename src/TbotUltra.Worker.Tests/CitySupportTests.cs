using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class CitySupportTests
{
    [Theory]
    [InlineData("<script>T4_feature_flags = {\"cities\":true};</script>", CityCapability.Enabled)]
    [InlineData("<script>T4_feature_flags = {\"cities\":false};</script>", CityCapability.Disabled)]
    [InlineData("<script>window.T4_feature_flags = {'cities':true}</script>", CityCapability.Enabled)]
    [InlineData("<script>const T4_feature_flags = {cities:false}</script>", CityCapability.Disabled)]
    [InlineData("<html></html>", CityCapability.Unknown)]
    public void CityCapabilityParser_UsesCitiesFeatureFlag(string html, CityCapability expected)
    {
        Assert.Equal(expected, CityCapabilityParser.Parse(html));
    }

    [Fact]
    public void WatchtowerParser_ReadsAvailableUpgrade()
    {
        var html = """
            <div class="extension">
              <strong>Watchtowers</strong><span class="level">Level 2</span>
              <div class="inlineIcon resource transfer"><span class="value">395</span></div>
              <div class="inlineIcon resource transfer"><span class="value">180</span></div>
              <div class="inlineIcon resource transfer"><span class="value">450</span></div>
              <div class="inlineIcon resource transfer"><span class="value">165</span></div>
              <div class="inlineIcon duration"><span class="value">0:06:20</span></div>
              <button value="Upgrade" class="textButtonV1 green" onclick="window.location.href='/build.php?id=40&amp;action=build'">Upgrade</button>
            </div>
            """;

        var parsed = WatchtowerDomParser.Parse(html, DateTimeOffset.UtcNow);

        Assert.True(parsed.ExtensionAvailable);
        Assert.True(parsed.UpgradeActionAvailable);
        Assert.Equal(2, parsed.Status?.Level);
        Assert.Equal(395, parsed.Status?.NextLevelWood);
        Assert.Equal(380, parsed.Status?.NextLevelBuildSeconds);
    }

    [Fact]
    public void WatchtowerParser_ReadsSeparateTwoSlotQueue()
    {
        var html = """
            <div class="extension"><strong>Watchtowers</strong><span class="level">Level 2 + 2</span></div>
            <h4 class="round">Under construction</h4>
            <table class="under_progress"><tbody>
              <tr><td class="desc">Watchtowers <span class="level">Level 3</span></td><td class="dur"><span data-value="370">0:06:10</span></td><td class="fin"><span>19:07</span></td></tr>
              <tr><td class="desc">Watchtowers <span class="level">Level 4</span></td><td class="dur"><span data-value="810">0:13:30</span></td><td class="fin"><span>19:14</span></td></tr>
            </tbody></table>
            """;

        var parsed = WatchtowerDomParser.Parse(html, DateTimeOffset.UtcNow);

        Assert.Equal(2, parsed.Status?.Active.Count);
        Assert.Equal(4, parsed.Status?.ProjectedLevel);
        Assert.True(parsed.Status?.QueueFull);
    }

    [Fact]
    public void WatchtowerCatalog_ContainsAllExactResourceLevels()
    {
        Assert.Equal(20, WatchtowerCatalogService.Levels.Count);
        Assert.Equal((240, 110, 275, 100), ToCosts(WatchtowerCatalogService.Level(1)!));
        Assert.Equal((26135, 11980, 29945, 10890), ToCosts(WatchtowerCatalogService.Level(20)!));
        Assert.Equal(6, WatchtowerCatalogService.Level(2)?.Population);
        Assert.Equal(4, WatchtowerCatalogService.Level(2)?.CulturePoints);
        Assert.InRange(WatchtowerCatalogService.BuildSecondsFor(3, 5, 20), 379, 381);
        Assert.InRange(WatchtowerCatalogService.BuildSecondsFor(5, 5, 20), 509, 511);
    }

    [Fact]
    public void Waterworks_Gating_IsConservativeForUnknownCapability()
    {
        Assert.True(BuildingCatalogService.CanConstructInVillage(
            45, false, "Egyptians", CityCapability.Unknown, CityStatus.Unknown, out _));
        Assert.False(BuildingCatalogService.CanConstructInVillage(
            45, false, "Egyptians", CityCapability.Enabled, CityStatus.Village, out var reason));
        Assert.Contains("City", reason);
    }

    private static (int, int, int, int) ToCosts(WatchtowerCatalogLevel level) =>
        (level.Wood, level.Clay, level.Iron, level.Crop);
}
