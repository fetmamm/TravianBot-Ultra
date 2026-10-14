using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroOasisRaidEstimatorTests
{
    [Fact]
    public void TryEstimate_UsesRaidCasualtyFormulaAndRoundsDamageUp()
    {
        Assert.True(NatureTroopCatalog.TryParseGarrison("Bear 1", out var garrison));

        Assert.True(HeroOasisRaidEstimator.TryEstimate(
            garrison, heroAttackStrength: 300, currentHpPercent: 100,
            heroMounted: false, out var estimate));

        Assert.NotNull(estimate);
        Assert.InRange(estimate.AttackerLossFraction, 0.25, 0.27);
        Assert.Equal(73, estimate.PredictedHpAfterRaid);
        Assert.True(estimate.EstimatedBountyPerResource >= 0);
    }

    [Fact]
    public void TryEstimate_MountedHeroUsesCavalryDefence()
    {
        Assert.True(NatureTroopCatalog.TryParseGarrison("Bear 1", out var garrison));

        Assert.True(HeroOasisRaidEstimator.TryEstimate(garrison, 300, 100, false, out var infantry));
        Assert.True(HeroOasisRaidEstimator.TryEstimate(garrison, 300, 100, true, out var cavalry));

        Assert.True(cavalry!.PredictedHpLoss > infantry!.PredictedHpLoss);
    }

    // Minimal regression data from seven captured Official Travian raid-simulator results.
    // The ignored docs/DOM captures are references only; tests must not depend on them.
    [Theory]
    [InlineData("Rat 50, Spider 50", 18)]
    [InlineData("Rat 50, Spider 20, Wild Boar 20, Bear 20", 39)]
    [InlineData("Wolf 55, Crocodile 13, Elephant 10", 68)]
    [InlineData("Rat 10, Spider 10, Snake 10, Bat 10, Wild Boar 10, Wolf 10, Bear 10, Crocodile 10, Tiger 10, Elephant 10", 70)]
    [InlineData("Rat 250, Spider 125, Snake 55, Bat 12", 68)]
    [InlineData("Wild Boar 88, Wolf 75, Bear 60", 80)]
    [InlineData("Wild Boar 111, Wolf 80, Bear 60, Crocodile 22, Tiger 55, Elephant 20", 93)]
    public void TryEstimate_IsConservativeAgainstCapturedRaidSimulatorCases(string animals, int simulatorHpLoss)
    {
        Assert.True(NatureTroopCatalog.TryParseGarrison(animals, out var garrison));
        Assert.True(HeroOasisRaidEstimator.TryEstimate(garrison, 8100, 100, false, out var estimate));

        Assert.NotNull(estimate);
        Assert.Equal(simulatorHpLoss + 1, estimate.PredictedHpLoss);
    }

    [Theory]
    [InlineData("Wolf 32, Bear 44, Crocodile 20, Tiger 50, Elephant 12", 6500, 100, 8)]
    [InlineData("Rat 22, Spider 12, Bat 15, Tiger 6", 3780, 45, 4)]
    public void TryEstimate_RemainsCloseForAdditionalSimulatorCases(
        string animals, int strength, int startingHp, int simulatorEndingHp)
    {
        Assert.True(NatureTroopCatalog.TryParseGarrison(animals, out var garrison));
        Assert.True(HeroOasisRaidEstimator.TryEstimate(garrison, strength, startingHp, false, out var estimate));

        Assert.NotNull(estimate);
        Assert.InRange(Math.Abs(estimate.PredictedHpAfterRaid - simulatorEndingHp), 0, 1);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(100, 101)]
    public void TryEstimate_RejectsInvalidHeroInputs(int heroStrength, int hp)
    {
        Assert.True(NatureTroopCatalog.TryParseGarrison("Bear 1", out var garrison));
        Assert.False(HeroOasisRaidEstimator.TryEstimate(garrison, heroStrength, hp, false, out _));
    }
}
