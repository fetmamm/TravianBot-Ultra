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
}
