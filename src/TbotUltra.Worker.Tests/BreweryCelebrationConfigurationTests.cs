using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BreweryCelebrationConfigurationTests
{
    [Fact]
    public void FromConfiguration_BreweryRestartDelay_DefaultsToFiveThroughFortyMinutes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal(5, options.BreweryCelebrationRestartDelayMinMinutes);
        Assert.Equal(40, options.BreweryCelebrationRestartDelayMaxMinutes);
        Assert.True(options.BreweryCelebrationRestartDelayEnabled);
        Assert.True(options.TownHallCelebrationRestartDelayEnabled);
        Assert.True(options.HeroAdventureRestartDelayEnabled);
        Assert.Equal(5, options.HeroAdventureRestartDelayMinMinutes);
        Assert.Equal(20, options.HeroAdventureRestartDelayMaxMinutes);
        Assert.True(options.SmithyUpgradeRestartDelayEnabled);
        Assert.Equal(10, options.SmithyUpgradeRestartDelayMinMinutes);
        Assert.Equal(30, options.SmithyUpgradeRestartDelayMaxMinutes);
    }

    [Fact]
    public void FromConfiguration_BreweryRestartDelay_ReadsConfiguredRange()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BotOptionPayloadKeys.BreweryCelebrationRestartDelayMinMinutes] = "8",
                [BotOptionPayloadKeys.BreweryCelebrationRestartDelayMaxMinutes] = "27",
                [BotOptionPayloadKeys.BreweryCelebrationRestartDelayEnabled] = "false",
                [BotOptionPayloadKeys.TownHallCelebrationRestartDelayEnabled] = "false",
                [BotOptionPayloadKeys.HeroAdventureRestartDelayEnabled] = "false",
                [BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes] = "6",
                [BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes] = "18",
                [BotOptionPayloadKeys.SmithyUpgradeRestartDelayEnabled] = "false",
                [BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes] = "12",
                [BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes] = "24",
            })
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal(8, options.BreweryCelebrationRestartDelayMinMinutes);
        Assert.Equal(27, options.BreweryCelebrationRestartDelayMaxMinutes);
        Assert.False(options.BreweryCelebrationRestartDelayEnabled);
        Assert.False(options.TownHallCelebrationRestartDelayEnabled);
        Assert.False(options.HeroAdventureRestartDelayEnabled);
        Assert.Equal(6, options.HeroAdventureRestartDelayMinMinutes);
        Assert.Equal(18, options.HeroAdventureRestartDelayMaxMinutes);
        Assert.False(options.SmithyUpgradeRestartDelayEnabled);
        Assert.Equal(12, options.SmithyUpgradeRestartDelayMinMinutes);
        Assert.Equal(24, options.SmithyUpgradeRestartDelayMaxMinutes);
    }

    [Fact]
    public void ResolveRestartDelaySeconds_AddsConfiguredMinutesAsSeconds()
    {
        Assert.Equal(300, TravianClient.ResolveRestartDelaySeconds(5, 5));
    }
}
