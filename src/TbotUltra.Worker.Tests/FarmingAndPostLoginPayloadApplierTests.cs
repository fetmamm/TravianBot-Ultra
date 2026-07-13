using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmingAndPostLoginPayloadApplierTests
{
    [Fact]
    public void Apply_MapsFarmingAndPostLoginDomains()
    {
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ContinuousFarmListNames] = "B,A,b",
            [BotOptionPayloadKeys.ContinuousFarmListIds] = "2,1,2",
            [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes] = "0",
            [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes] = "0",
            [BotOptionPayloadKeys.ContinuousFarmSendMode] = "all_at_once",
            [BotOptionPayloadKeys.TownHallCelebrationMode] = "great",
            [BotOptionPayloadKeys.ContinuousFarmDeactivateLosses] = "true",
            [BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses] = "true",
            [BotOptionPayloadKeys.ContinuousFarmNextListIndex] = "-4",
            [BotOptionPayloadKeys.PostLoginAnalyzeFarmlists] = "false",
            [BotOptionPayloadKeys.PostLoginAnalyzeHero] = "false",
            [BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory] = "false",
            [BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue] = "false",
            [BotOptionPayloadKeys.PostLoginAnalyzeBrewery] = "false",
            [BotOptionPayloadKeys.PostLoginAnalyzeNewVillages] = "false",
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = "false",
        };

        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), payload);

        Assert.Equal(new[] { "B", "A" }, result.ContinuousFarmListNames);
        Assert.Equal(new[] { "2", "1" }, result.ContinuousFarmListIds);
        Assert.Equal(FarmingDefaults.DefaultDispatchDelayMinMinutes, result.ContinuousFarmDispatchDelayMinMinutes);
        Assert.Equal(FarmingDefaults.DefaultDispatchDelayMaxMinutes, result.ContinuousFarmDispatchDelayMaxMinutes);
        Assert.Equal(FarmingDefaults.SendModeAllAtOnce, result.ContinuousFarmSendMode);
        Assert.Equal(TownHallCelebrationDefaults.Big, result.TownHallCelebrationMode);
        Assert.True(result.ContinuousFarmDeactivateLosses);
        Assert.True(result.ContinuousFarmDeactivateOasisLosses);
        Assert.Equal(0, result.ContinuousFarmNextListIndex);
        Assert.False(result.PostLoginAnalyzeFarmlists);
        Assert.False(result.PostLoginAnalyzeHero);
        Assert.False(result.PostLoginAnalyzeHeroInventory);
        Assert.False(result.PostLoginReadTroopTrainingQueue);
        Assert.False(result.PostLoginAnalyzeBrewery);
        Assert.False(result.PostLoginAnalyzeNewVillages);
        Assert.False(result.AutomaticallyCheckLanguage);
    }
}
