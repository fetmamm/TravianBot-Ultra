using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class AdventureVideoChanceDecisionTests
{
    [Theory]
    [InlineData(70, 69, true)]
    [InlineData(70, 70, false)]
    [InlineData(0, 0, false)]
    [InlineData(100, 99, true)]
    public void Evaluate_RunsOnlyWhenRollIsBelowChance(int chance, int roll, bool expected)
    {
        var result = AdventureVideoChanceDecision.Evaluate(chance, roll);

        Assert.Equal(expected, result.RunVideo);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(130, 100)]
    public void Evaluate_ClampsChance(int configured, int expected)
    {
        var result = AdventureVideoChanceDecision.Evaluate(configured, roll: 50);

        Assert.Equal(expected, result.ChancePercent);
    }
}
