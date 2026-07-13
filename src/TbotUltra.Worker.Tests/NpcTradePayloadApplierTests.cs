using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class NpcTradePayloadApplierTests
{
    [Fact]
    public void Apply_MapsWholeNpcDomainAndPreservesAllowedTimeLimit()
    {
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.NpcTradeEnabled] = "true",
            [BotOptionPayloadKeys.NpcTradeConstructionEnabled] = "true",
            [BotOptionPayloadKeys.NpcTradeThresholdPercent] = "101",
            [BotOptionPayloadKeys.NpcTradeAnalyzeWood] = "false",
            [BotOptionPayloadKeys.NpcTradeAnalyzeClay] = "false",
            [BotOptionPayloadKeys.NpcTradeAnalyzeIron] = "true",
            [BotOptionPayloadKeys.NpcTradeAnalyzeCrop] = "true",
            [BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled] = "true",
            [BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds] = "1200",
        };

        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), payload);

        Assert.True(result.NpcTradeEnabled);
        Assert.True(result.NpcTradeConstructionEnabled);
        Assert.Equal(100, result.NpcTradeThresholdPercent);
        Assert.False(result.NpcTradeAnalyzeWood);
        Assert.False(result.NpcTradeAnalyzeClay);
        Assert.True(result.NpcTradeAnalyzeIron);
        Assert.True(result.NpcTradeAnalyzeCrop);
        Assert.True(result.NpcTradeBuildTimeLimitEnabled);
        Assert.Equal(1200, result.NpcTradeBuildTimeLimitSeconds);
    }

    [Theory]
    [InlineData("29", 60)]
    [InlineData("3600", 3600)]
    public void Apply_NormalizesBuildTimeLimitExactlyAsBefore(string value, int expected)
    {
        var result = BotOptionsPayloadApplier.Apply(new BotOptions(), new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds] = value,
        });

        Assert.Equal(expected, result.NpcTradeBuildTimeLimitSeconds);
    }
}
