using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroPayloadApplierTests
{
    [Fact]
    public void Apply_MapsHeroAdventureCollectAndResourceDomains()
    {
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = "71",
            [BotOptionPayloadKeys.HeroAutoRevive] = "false",
            [BotOptionPayloadKeys.HeroAutoAssignPoints] = "false",
            [BotOptionPayloadKeys.HeroAutoUseOintments] = "true",
            [BotOptionPayloadKeys.HeroStatPriority] = "resources,offence_bonus",
            [BotOptionPayloadKeys.HeroAdventurePickOrder] = "hardest",
            [BotOptionPayloadKeys.HeroContinuousAdventures] = "true",
            [BotOptionPayloadKeys.IncreaseAdventuresToHard] = "true",
            [BotOptionPayloadKeys.ReduceAdventureTime] = "true",
            [BotOptionPayloadKeys.HeroAdventureVideoChancePercent] = "150",
            [BotOptionPayloadKeys.AutoCollectTasksEnabled] = "false",
            [BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled] = "false",
            [BotOptionPayloadKeys.ProductionBonusVideoEnabled] = "false",
            [BotOptionPayloadKeys.CollectStepDelayMinSeconds] = "-2",
            [BotOptionPayloadKeys.CollectStepDelayMaxSeconds] = "5000",
            [BotOptionPayloadKeys.HeroResourceTransferEnabled] = "true",
            [BotOptionPayloadKeys.HeroResourceMaxUseEnabled] = "false",
            [BotOptionPayloadKeys.HeroResourceMaxUsePerResource] = "-5",
            [BotOptionPayloadKeys.HeroResourceUseConstruction] = "false",
            [BotOptionPayloadKeys.HeroResourceUseSmithy] = "true",
            [BotOptionPayloadKeys.HeroResourceUseBrewery] = "true",
            [BotOptionPayloadKeys.HeroResourceUseTownHall] = "true",
        };

        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), payload);

        Assert.Equal(71, result.HeroMinHpForAdventure);
        Assert.False(result.HeroAutoRevive);
        Assert.False(result.HeroAutoAssignPoints);
        Assert.True(result.HeroAutoUseOintments);
        Assert.Equal("resources,offence_bonus", result.HeroStatPriority);
        Assert.Equal("hardest", result.HeroAdventurePickOrder);
        Assert.True(result.HeroContinuousAdventures);
        Assert.True(result.IncreaseAdventuresToHard);
        Assert.True(result.ReduceAdventureTime);
        Assert.Equal(100, result.HeroAdventureVideoChancePercent);
        Assert.False(result.AutoCollectTasksEnabled);
        Assert.False(result.AutoCollectDailyQuestsEnabled);
        Assert.False(result.ProductionBonusVideoEnabled);
        Assert.Equal(0, result.CollectStepDelayMinSeconds);
        Assert.Equal(3600, result.CollectStepDelayMaxSeconds);
        Assert.True(result.HeroResourceTransferEnabled);
        Assert.False(result.HeroResourceMaxUseEnabled);
        Assert.Equal(0, result.HeroResourceMaxUsePerResource);
        Assert.False(result.HeroResourceUseConstruction);
        Assert.True(result.HeroResourceUseSmithy);
        Assert.True(result.HeroResourceUseBrewery);
        Assert.True(result.HeroResourceUseTownHall);
    }
}
