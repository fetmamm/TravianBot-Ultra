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
    }

    [Fact]
    public void FromConfiguration_BreweryRestartDelay_ReadsConfiguredRange()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BotOptionPayloadKeys.BreweryCelebrationRestartDelayMinMinutes] = "8",
                [BotOptionPayloadKeys.BreweryCelebrationRestartDelayMaxMinutes] = "27",
            })
            .Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal(8, options.BreweryCelebrationRestartDelayMinMinutes);
        Assert.Equal(27, options.BreweryCelebrationRestartDelayMaxMinutes);
    }

    [Fact]
    public void ResolveCelebrationRestartDelaySeconds_AddsConfiguredMinutesAsSeconds()
    {
        Assert.Equal(300, TravianClient.ResolveCelebrationRestartDelaySeconds(5, 5));
    }
}
