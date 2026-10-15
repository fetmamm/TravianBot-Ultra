using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingOptionsTests
{
    [Fact]
    public void Defaults_AreDisabledAndExcludeCropOnlyOases()
    {
        var options = BotOptionsFactory.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection().Build());

        Assert.False(options.HeroRaidingEnabled);
        Assert.Equal(40, options.HeroRaidingMinimumHpPercent);
        Assert.Equal(20, options.HeroRaidingMaximumDistance);
        Assert.Equal(10, options.HeroRaidingObservationMinutes);
        Assert.Equal(3, options.HeroRaidingMinimumCandidates);
        Assert.Equal("raiding", options.HeroRaidingPriority);
        Assert.Contains("Wood+Crop", options.HeroRaidingSelectedTypes);
        Assert.DoesNotContain("Crop25", options.HeroRaidingSelectedTypes);
        Assert.DoesNotContain("Crop50", options.HeroRaidingSelectedTypes);
    }

    [Fact]
    public void Configuration_ClampsUnsafeValuesAndPreservesExplicitTypes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [BotOptionPayloadKeys.HeroRaidingEnabled] = "true",
                [BotOptionPayloadKeys.HeroRaidingMinimumHpPercent] = "0",
                [BotOptionPayloadKeys.HeroRaidingMaximumDistance] = "500",
                [BotOptionPayloadKeys.HeroRaidingObservationMinutes] = "500",
                [BotOptionPayloadKeys.HeroRaidingMinimumCandidates] = "500",
                [BotOptionPayloadKeys.HeroRaidingSelectedTypes] = "Crop,Clay+Crop,Unknown,Crop",
                [BotOptionPayloadKeys.HeroRaidingPriority] = "adventures",
            }).Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.True(options.HeroRaidingEnabled);
        Assert.Equal(1, options.HeroRaidingMinimumHpPercent);
        Assert.Equal(50, options.HeroRaidingMaximumDistance);
        Assert.Equal(120, options.HeroRaidingObservationMinutes);
        Assert.Equal(20, options.HeroRaidingMinimumCandidates);
        Assert.Equal("Crop25,Crop50,Clay+Crop", options.HeroRaidingSelectedTypes);
        Assert.Equal("adventures", options.HeroRaidingPriority);
    }

    [Fact]
    public void Configuration_PreservesIndependentCropChoices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [BotOptionPayloadKeys.HeroRaidingSelectedTypes] = "Crop50,Wood+Crop",
            }).Build();

        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal("Crop50,Wood+Crop", options.HeroRaidingSelectedTypes);
    }
}
