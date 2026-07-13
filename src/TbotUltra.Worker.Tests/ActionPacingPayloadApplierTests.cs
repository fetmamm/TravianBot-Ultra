using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ActionPacingPayloadApplierTests
{
    [Fact]
    public void Apply_MapsEveryActionPacingPayloadKey()
    {
        var source = new BotOptions();
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ActionPacingEnabled] = "false",
            [BotOptionPayloadKeys.ActionPacingTaskMinSeconds] = "1",
            [BotOptionPayloadKeys.ActionPacingTaskMaxSeconds] = "2",
            [BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds] = "3",
            [BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds] = "4",
            [BotOptionPayloadKeys.ActionPacingClickMinSeconds] = "5",
            [BotOptionPayloadKeys.ActionPacingClickMaxSeconds] = "6",
            [BotOptionPayloadKeys.ActionPacingLoopMinSeconds] = "7",
            [BotOptionPayloadKeys.ActionPacingLoopMaxSeconds] = "8",
            [BotOptionPayloadKeys.FarmListStepDelayMinSeconds] = "9",
            [BotOptionPayloadKeys.FarmListStepDelayMaxSeconds] = "10",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.False(result.ActionPacingEnabled);
        Assert.Equal(1, result.ActionPacingTaskMinSeconds);
        Assert.Equal(2, result.ActionPacingTaskMaxSeconds);
        Assert.Equal(3, result.ActionPacingPageLoadMinSeconds);
        Assert.Equal(4, result.ActionPacingPageLoadMaxSeconds);
        Assert.Equal(5, result.ActionPacingClickMinSeconds);
        Assert.Equal(6, result.ActionPacingClickMaxSeconds);
        Assert.Equal(7, result.ActionPacingLoopMinSeconds);
        Assert.Equal(8, result.ActionPacingLoopMaxSeconds);
        Assert.Equal(9, result.FarmListStepDelayMinSeconds);
        Assert.Equal(10, result.FarmListStepDelayMaxSeconds);
    }

    [Fact]
    public void Apply_PreservesSourceForInvalidOrEmptyValuesAndClampsValidDelays()
    {
        var source = new BotOptions
        {
            ActionPacingEnabled = true,
            ActionPacingTaskMinSeconds = 12,
            ActionPacingTaskMaxSeconds = 13,
        };
        var payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.ActionPacingEnabled] = "invalid",
            [BotOptionPayloadKeys.ActionPacingTaskMinSeconds] = " ",
            [BotOptionPayloadKeys.ActionPacingTaskMaxSeconds] = "5000",
            [BotOptionPayloadKeys.ActionPacingClickMinSeconds] = "-1",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.True(result.ActionPacingEnabled);
        Assert.Equal(12, result.ActionPacingTaskMinSeconds);
        Assert.Equal(3600, result.ActionPacingTaskMaxSeconds);
        Assert.Equal(0, result.ActionPacingClickMinSeconds);
    }
}
