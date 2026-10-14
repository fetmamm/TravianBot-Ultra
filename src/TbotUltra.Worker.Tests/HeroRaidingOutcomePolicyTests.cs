using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingOutcomePolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, null, HeroRaidingOutcomeKind.Disable)]
    [InlineData(true, false, false, false, null, HeroRaidingOutcomeKind.Disable)]
    [InlineData(true, true, false, true, null, HeroRaidingOutcomeKind.Disable)]
    [InlineData(true, true, false, false, null, HeroRaidingOutcomeKind.Pending)]
    [InlineData(true, true, true, false, null, HeroRaidingOutcomeKind.Disable)]
    [InlineData(true, true, true, false, 39, HeroRaidingOutcomeKind.Disable)]
    [InlineData(true, true, true, false, 48, HeroRaidingOutcomeKind.Disable)]
    [InlineData(true, true, true, false, 49, HeroRaidingOutcomeKind.Verified)]
    [InlineData(true, true, true, false, 50, HeroRaidingOutcomeKind.Verified)]
    public void Evaluate_RequiresConfirmedSafeReturn(
        bool outgoingVerified,
        bool stateKnown,
        bool home,
        bool dead,
        int? hp,
        HeroRaidingOutcomeKind expected)
    {
        var observation = new HeroRaidingOutcomeObservation(outgoingVerified, stateKnown, home, dead, hp);

        var decision = HeroRaidingOutcomePolicy.Evaluate(observation, predictedHpAfterRaid: 50,
            minimumHpPercent: 40);

        Assert.Equal(expected, decision.Kind);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Evaluate_AbsoluteMinimumOverridesRoundingTolerance()
    {
        var observation = new HeroRaidingOutcomeObservation(true, true, true, false, 39);

        Assert.Equal(HeroRaidingOutcomeKind.Disable,
            HeroRaidingOutcomePolicy.Evaluate(observation, predictedHpAfterRaid: 40,
                minimumHpPercent: 40).Kind);
    }
}
