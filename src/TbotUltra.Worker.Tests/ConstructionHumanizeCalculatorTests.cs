using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionHumanizeCalculatorTests
{
    [Fact]
    public void CalculateAfterFullQueue_UsesRemainingSecondTimerAfterFirstFinishes()
    {
        var result = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            [100, 500], 100, 5, 20, 25, 1, 3, (_, _) => 10);

        Assert.Equal(40, result.DelaySeconds);
        Assert.Equal("after slot opens, percent 10% of 400s remaining", result.Reason);
    }

    [Fact]
    public void CalculateAfterFullQueue_CapsPercentageDelay()
    {
        var result = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            [100, 10_100], 100, 5, 20, 2, 1, 3, (_, _) => 20);

        Assert.Equal(120, result.DelaySeconds);
    }

    [Fact]
    public void CalculateAfterFullQueue_UsesNoPlusRangeWhenNoTimerRemains()
    {
        var result = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            [100], 100, 5, 20, 25, 1, 3, (min, max) =>
            {
                Assert.Equal(1, min);
                Assert.Equal(3, max);
                return 2.5;
            });

        Assert.Equal(150, result.DelaySeconds);
        Assert.Equal($"after slot opens, no-plus {2.5:F1}m", result.Reason);
    }

    [Fact]
    public void CalculateAfterFullQueue_ReturnsNoDelayForInvalidSlotWaitWithoutCallingRandom()
    {
        var result = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            [100], 0, 5, 20, 25, 1, 3, (_, _) => throw new Xunit.Sdk.XunitException("RNG should not be called"));

        Assert.Equal(ConstructionHumanizeDecision.None, result);
    }

    [Fact]
    public void CalculateAfterFullQueue_UsesShortestRemainingTimerAsReference()
    {
        var result = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            [900, 100, 500], 100, 5, 20, 25, 1, 3, (_, _) => 10);

        Assert.Equal(40, result.DelaySeconds);
    }
}
