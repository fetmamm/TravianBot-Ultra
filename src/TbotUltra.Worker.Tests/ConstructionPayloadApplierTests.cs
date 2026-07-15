using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionPayloadApplierTests
{
    [Fact]
    public void Apply_MapsConstructionDomainAndPreservesNormalization()
    {
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.TargetVillageName] = "Village",
            [BotOptionPayloadKeys.TargetVillageUrl] = "/dorf1.php?newdid=1",
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = "4",
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "9",
            [BotOptionPayloadKeys.ResourceUpgradeMaxAttempts] = "8",
            [BotOptionPayloadKeys.ResourceBuildStrategy] = "unknown",
            [BotOptionPayloadKeys.SmithyUpgradeTargets] = "u1=5",
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = "20",
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = "10",
            [BotOptionPayloadKeys.BuildingUpgradeMaxAttempts] = "7",
            [BotOptionPayloadKeys.BuildingConstructSlotId] = "21",
            [BotOptionPayloadKeys.BuildingConstructGid] = "23",
            [BotOptionPayloadKeys.BuildingConstructName] = "Cranny",
            [BotOptionPayloadKeys.BuildingConstructAllowSlotFallback] = "true",
            [BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots] = "19,31",
            [BotOptionPayloadKeys.ConstructFasterEnabled] = "true",
            [BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled] = "false",
            [BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] = "-1",
            [BotOptionPayloadKeys.ConstructFasterRandomEnabled] = "true",
            [BotOptionPayloadKeys.ConstructFasterRandomChancePercent] = "101",
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = "Main Building",
            [BotOptionPayloadKeys.TargetLevel] = "3",
            [BotOptionPayloadKeys.UpgradeSelectorProfile] = "strict_green",
        };

        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), payload);

        Assert.Equal("Village", result.TargetVillageName);
        Assert.Equal("/dorf1.php?newdid=1", result.TargetVillageUrl);
        Assert.Equal(4, result.ResourceUpgradeSlotId);
        Assert.Equal(9, result.ResourceUpgradeTargetLevel);
        Assert.Equal(8, result.ResourceUpgradeMaxAttempts);
        Assert.Equal("lowest_first", result.ResourceBuildStrategy);
        Assert.Equal("u1=5", result.SmithyUpgradeTargets);
        Assert.Equal(20, result.BuildingUpgradeSlotId);
        Assert.Equal(10, result.BuildingUpgradeTargetLevel);
        Assert.Equal(7, result.BuildingUpgradeMaxAttempts);
        Assert.Equal(21, result.BuildingConstructSlotId);
        Assert.Equal(23, result.BuildingConstructGid);
        Assert.Equal("Cranny", result.BuildingConstructName);
        Assert.True(result.BuildingConstructAllowSlotFallback);
        Assert.Equal("19,31", result.BuildingConstructFallbackExcludedSlots);
        Assert.True(result.ConstructFasterEnabled);
        Assert.False(result.ConstructFasterMinBuildTimeEnabled);
        Assert.Equal(0, result.ConstructFasterMinBuildMinutes);
        Assert.True(result.ConstructFasterRandomEnabled);
        Assert.Equal(100, result.ConstructFasterRandomChancePercent);
        Assert.Equal("Main Building", result.TargetBuildingSlotOrName);
        Assert.Equal(3, result.TargetLevel);
        Assert.Equal("strict_green", result.UpgradeSelectorProfile);
    }
}
