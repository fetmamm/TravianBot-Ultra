using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ActionPacingPayloadApplierTests
{
    [Theory]
    [InlineData(null, 60)]
    [InlineData("20", 20)]
    [InlineData("60", 60)]
    [InlineData("90", 90)]
    [InlineData("45", 60)]
    public void FromConfiguration_NormalizesShortVillageDeferSeconds(string? configured, int expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "srv",
            ["base_url"] = "https://example.com",
        };
        if (configured is not null)
        {
            values[BotOptionPayloadKeys.ShortVillageDeferSeconds] = configured;
        }

        var options = BotOptionsFactory.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.Equal(expected, options.ShortVillageDeferSeconds);
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData("5", 5)]
    [InlineData("10", 10)]
    [InlineData("15", 15)]
    [InlineData("30", 30)]
    [InlineData("20", 15)]
    public void FromConfiguration_NormalizesVillageRoundSleepExtension(string? configured, int expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["server_name"] = "srv",
            ["base_url"] = "https://example.com",
        };
        if (configured is not null)
            values[BotOptionPayloadKeys.VillageRoundSleepExtensionMinutes] = configured;

        var options = BotOptionsFactory.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.Equal(expected, options.VillageRoundSleepExtensionMinutes);
    }

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
            [BotOptionPayloadKeys.ShortVillageDeferSeconds] = "90",
            [BotOptionPayloadKeys.VillageRoundSleepExtensionMinutes] = "30",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.True(result.ActionPacingEnabled);
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
        Assert.Equal(90, result.ShortVillageDeferSeconds);
        Assert.Equal(30, result.VillageRoundSleepExtensionMinutes);
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
            [BotOptionPayloadKeys.ShortVillageDeferSeconds] = "45",
        };

        var result = BotOptionsPayloadApplier.Apply(source, payload);

        Assert.True(result.ActionPacingEnabled);
        Assert.Equal(12, result.ActionPacingTaskMinSeconds);
        Assert.Equal(3600, result.ActionPacingTaskMaxSeconds);
        Assert.Equal(0, result.ActionPacingClickMinSeconds);
        Assert.Equal(60, result.ShortVillageDeferSeconds);
    }
}
