using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BotOptionsPayloadApplierTests
{
    [Fact]
    public void Apply_OverridesConfiguredFields_FromPayload()
    {
        var source = new BotOptions
        {
            ServerName = "srv",
            BaseUrl = "https://example.com",
            LoginPath = "/login.php",
            VillageOverviewPath = "/dorf1.php",
            ResourceUpgradeSlotId = 1,
            ResourceUpgradeTargetLevel = 2,
            BuildingUpgradeSlotId = 3,
            BuildingUpgradeTargetLevel = 4,
            BuildingConstructSlotId = 5,
            BuildingConstructGid = 6,
            BuildingConstructName = "Main Building",
            TargetBuildingSlotOrName = "20",
            TargetLevel = 5,
            HeroMinHpForAdventure = 60,
            HeroAutoRevive = true,
            HeroStatPriority = "offense,resource,regeneration",
            UpgradeSelectorProfile = "auto",
        };

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = "9",
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "10",
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = "11",
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = "12",
            [BotOptionPayloadKeys.BuildingConstructSlotId] = "13",
            [BotOptionPayloadKeys.BuildingConstructGid] = "14",
            [BotOptionPayloadKeys.BuildingConstructName] = "Barracks",
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = "Main Building",
            [BotOptionPayloadKeys.TargetLevel] = "10",
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = "75",
            [BotOptionPayloadKeys.HeroAutoRevive] = "false",
            [BotOptionPayloadKeys.HeroStatPriority] = "resource,offense",
            [BotOptionPayloadKeys.UpgradeSelectorProfile] = "strict_green",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.Equal(9, result.ResourceUpgradeSlotId);
        Assert.Equal(10, result.ResourceUpgradeTargetLevel);
        Assert.Equal(11, result.BuildingUpgradeSlotId);
        Assert.Equal(12, result.BuildingUpgradeTargetLevel);
        Assert.Equal(13, result.BuildingConstructSlotId);
        Assert.Equal(14, result.BuildingConstructGid);
        Assert.Equal("Barracks", result.BuildingConstructName);
        Assert.Equal("Main Building", result.TargetBuildingSlotOrName);
        Assert.Equal(10, result.TargetLevel);
        Assert.Equal(75, result.HeroMinHpForAdventure);
        Assert.False(result.HeroAutoRevive);
        Assert.Equal("resource,offense", result.HeroStatPriority);
        Assert.Equal("strict_green", result.UpgradeSelectorProfile);
    }
}
