using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroStatusDecisionTests
{
    [Fact]
    public void ResolveAdventureCount_PrefersAuthoritativeSidebarSignal()
    {
        Assert.Equal(2, HeroStatusDecision.ResolveAdventureCount(true, 2, 7));
        Assert.Equal(7, HeroStatusDecision.ResolveAdventureCount(false, 2, 7));
    }

    [Fact]
    public void AdventureCounts_AreNeverNegative()
    {
        Assert.Equal(0, HeroStatusDecision.ResolveAdventureCount(true, -1, 4));
        Assert.Equal(0, HeroStatusDecision.TryResolveAdventureCount(false, 0, true, -2));
    }

    [Fact]
    public void TryResolveAdventureCount_ReturnsUnknownWithoutAnyStatusSource()
    {
        Assert.Null(HeroStatusDecision.TryResolveAdventureCount(false, 0, false, 0));
    }

    [Theory]
    [InlineData("Hero is dead")]
    [InlineData("Hero deceased")]
    public void IsDeadStatusText_RecognizesExistingEnglishSignals(string text)
    {
        Assert.True(HeroStatusDecision.IsDeadStatusText(text));
    }

    [Theory]
    [InlineData("Hero is on the way")]
    [InlineData("Hero is on its way")]
    [InlineData("Arrival in 00:10:00")]
    [InlineData("Hero is back from adventure")]
    [InlineData("Returning")]
    public void IsAwayStatusText_RecognizesExistingEnglishSignals(string text)
    {
        Assert.True(HeroStatusDecision.IsAwayStatusText(text));
    }

    [Fact]
    public void StatusTextClassifiers_DoNotGuessFromUnrelatedText()
    {
        Assert.False(HeroStatusDecision.IsDeadStatusText("Hero is at home"));
        Assert.False(HeroStatusDecision.IsAwayStatusText(null));
    }

    [Theory]
    [InlineData(420, true, 420)]
    [InlineData(null, true, 1800)]
    [InlineData(null, false, 900)]
    public void ResolveAwayRetrySeconds_PreservesEtaAndUsesReinforcementFallback(
        int? returnSeconds,
        bool isReinforcing,
        int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, HeroStatusDecision.ResolveAwayRetrySeconds(returnSeconds, isReinforcing));
    }

    [Theory]
    [InlineData(40, 60, 40, 43200)]
    [InlineData(59, 60, 40, 2160)]
    [InlineData(0, 100, 20, 432000)]
    public void ComputeHpWaitSeconds_UsesConfiguredDailyRegeneration(
        int hp,
        int threshold,
        int regenPerDay,
        int expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            HeroStatusDecision.ComputeHpWaitSeconds(hp, threshold, regenPerDay, 7 * 24 * 60 * 60));
    }

    [Fact]
    public void ResolveIsInVillage_RunningSignalWinsOverStaleHomeSignal()
    {
        Assert.False(HeroStatusDecision.ResolveIsInVillage(
            reinforcing: false,
            running: true,
            home: true,
            legacyStatus: null,
            officialAway: false,
            sidebarText: null));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, true)]
    public void ResolveIsInVillage_UsesStrongSignalsAndKeepsUnknownNonBlocking(
        bool reinforcing,
        bool running,
        bool home,
        bool expected)
    {
        Assert.Equal(expected, HeroStatusDecision.ResolveIsInVillage(
            reinforcing,
            running,
            home,
            legacyStatus: null,
            officialAway: false,
            sidebarText: null));
    }

    [Fact]
    public void AdventureDispatchConfirmation_AcceptsAuthoritativeAwayStatusAfterRedirect()
    {
        Assert.True(HeroStatusDecision.IsAdventureDispatchConfirmed(
            activeAdventurePage: false,
            isInVillage: false,
            isDead: false,
            isReviving: false));
        Assert.False(HeroStatusDecision.IsAdventureDispatchConfirmed(
            activeAdventurePage: false,
            isInVillage: true,
            isDead: false,
            isReviving: false));
    }

}
