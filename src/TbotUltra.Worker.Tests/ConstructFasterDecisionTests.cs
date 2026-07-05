using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructFasterDecisionTests
{
    [Fact]
    public void Evaluate_DisabledFeature_Skips()
    {
        var result = ConstructFasterDecision.Evaluate(
            new BotOptions { ConstructFasterEnabled = false },
            durationSeconds: 7200,
            buttonPresent: true,
            buttonDisabled: false);

        Assert.False(result.UseVideo);
        Assert.Equal("feature disabled", result.Reason);
    }

    [Fact]
    public void Evaluate_DurationAtThreshold_Skips()
    {
        var result = ConstructFasterDecision.Evaluate(
            new BotOptions { ConstructFasterEnabled = true, ConstructFasterMinBuildTimeEnabled = true, ConstructFasterMinBuildMinutes = 30 },
            durationSeconds: 1800,
            buttonPresent: true,
            buttonDisabled: false);

        Assert.False(result.UseVideo);
    }

    [Fact]
    public void Evaluate_MinBuildTimeDisabled_UsesVideoRegardlessDuration()
    {
        var result = ConstructFasterDecision.Evaluate(
            new BotOptions { ConstructFasterEnabled = true, ConstructFasterMinBuildTimeEnabled = false, ConstructFasterMinBuildMinutes = 30 },
            durationSeconds: 10,
            buttonPresent: true,
            buttonDisabled: false);

        Assert.True(result.UseVideo);
    }

    [Theory]
    [InlineData(false, false, "video button missing")]
    [InlineData(true, true, "video button disabled")]
    public void Evaluate_ButtonUnavailable_Skips(bool present, bool disabled, string reason)
    {
        var result = ConstructFasterDecision.Evaluate(
            new BotOptions { ConstructFasterEnabled = true, ConstructFasterMinBuildMinutes = 30 },
            durationSeconds: 1801,
            buttonPresent: present,
            buttonDisabled: disabled);

        Assert.False(result.UseVideo);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void Evaluate_RandomChance_UsesRollBelowChance(int roll, bool expected)
    {
        var result = ConstructFasterDecision.Evaluate(
            new BotOptions
            {
                ConstructFasterEnabled = true,
                ConstructFasterMinBuildMinutes = 30,
                ConstructFasterRandomEnabled = true,
                ConstructFasterRandomChancePercent = 50,
            },
            durationSeconds: 1801,
            buttonPresent: true,
            buttonDisabled: false,
            randomRoll: roll);

        Assert.Equal(expected, result.UseVideo);
    }
}
