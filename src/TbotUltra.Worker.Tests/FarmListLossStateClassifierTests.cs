using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListLossStateClassifierTests
{
    [Theory]
    [InlineData("lastRaidState attack_lost_small", FarmListLossState.Loss)]
    [InlineData("lastRaidState attack_won_withLosses_small", FarmListLossState.Loss)]
    [InlineData("lastRaidState attack_won_withoutLosses_small", FarmListLossState.NoLoss)]
    [InlineData("", FarmListLossState.Unknown)]
    public void Classify_MapsTravianRaidClasses(string classNames, FarmListLossState expected)
    {
        Assert.Equal(expected, FarmListLossStateClassifier.Classify(classNames));
    }

    [Fact]
    public void IsUnoccupiedOasis_IgnoresCoordinatesAndBidiMarks()
    {
        Assert.True(FarmListLossStateClassifier.IsUnoccupiedOasis("Unoccupied oasis \u202d(12|-34)\u202c"));
        Assert.False(FarmListLossStateClassifier.IsUnoccupiedOasis("Occupied oasis (12|-34)"));
    }
}
