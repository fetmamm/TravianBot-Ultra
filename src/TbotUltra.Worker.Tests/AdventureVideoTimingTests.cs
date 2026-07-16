using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class AdventureVideoTimingTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(45)]
    [InlineData(59)]
    [InlineData(59.999)]
    public void AttemptCannotCompleteDuringProtectedMinute(double elapsedSeconds)
    {
        Assert.False(BonusVideoPlaybackPolicy.MayComplete(elapsedSeconds));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(60.001)]
    [InlineData(120)]
    public void AttemptMayCompleteAtSixtySecondsOrLater(double elapsedSeconds)
    {
        Assert.True(BonusVideoPlaybackPolicy.MayComplete(elapsedSeconds));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(45)]
    [InlineData(59)]
    public void ProviderTextCannotAbortWhileVideoMayStillWork(double elapsedSeconds)
    {
        Assert.False(BonusVideoPlaybackPolicy.MayAcceptProviderFailure(elapsedSeconds, 10, playerPresent: true));
        Assert.False(BonusVideoPlaybackPolicy.MayAcceptProviderFailure(elapsedSeconds, 10, playerPresent: false));
    }

    [Fact]
    public void ProviderFailureAfterProtectedMinuteNeedsRepeatedConfirmationWhilePlayerExists()
    {
        Assert.False(BonusVideoPlaybackPolicy.MayAcceptProviderFailure(60, 1, playerPresent: true));
        Assert.True(BonusVideoPlaybackPolicy.MayAcceptProviderFailure(60, 2, playerPresent: true));
    }

    [Fact]
    public void ProviderFailureAfterProtectedMinuteMayUseConfirmedMissingPlayer()
    {
        Assert.True(BonusVideoPlaybackPolicy.MayAcceptProviderFailure(60, 1, playerPresent: false));
    }

    [Fact]
    public void IsolatedActionBudgetCoversSlowPrePlayAndFullPostPlayTimeout()
    {
        Assert.True(
            BonusVideoPlaybackPolicy.IsolatedActionTimeoutSeconds
            >= BonusVideoPlaybackPolicy.PostPlayTimeoutSeconds + 60);
    }
}
