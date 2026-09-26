using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BonusVideoFailureClassifierTests
{
    [Theory]
    [InlineData("clay: +15% production video completed.")]
    [InlineData("Increased adventure danger activated; the bonus is now active.")]
    public void Classify_RecognizesSuccess(string message)
    {
        Assert.Equal(BonusVideoFailureKind.None, BonusVideoFailureClassifier.Classify(message));
    }

    [Theory]
    [InlineData("Are you using an ad blocker or declining third-party cookies?")]
    [InlineData("video player did not open (likely no ad available or blocked)")]
    [InlineData("ad provider visibly reported no ad, ad blocking, or rejected third-party cookies")]
    public void Classify_RecognizesAdAvailabilityFailure(string message)
    {
        var kind = BonusVideoFailureClassifier.Classify(message);

        Assert.Equal(BonusVideoFailureKind.NoAdOrCookies, kind);
        Assert.False(BonusVideoFailureClassifier.ShouldRetryImmediately(kind));
        Assert.Equal(TimeSpan.FromMinutes(20), BonusVideoFailureClassifier.Cooldown(kind));
    }

    [Fact]
    public void Classify_RecognizesTimeoutWithoutImmediateRetry()
    {
        var kind = BonusVideoFailureClassifier.Classify("video completion was not confirmed");

        Assert.Equal(BonusVideoFailureKind.Timeout, kind);
        Assert.False(BonusVideoFailureClassifier.ShouldRetryImmediately(kind));
        Assert.Equal(TimeSpan.FromMinutes(30), BonusVideoFailureClassifier.Cooldown(kind));
    }

    [Fact]
    public void IsolatedRunResult_ReportsRemainingCooldownSeconds()
    {
        var now = new DateTimeOffset(2026, 7, 15, 20, 17, 21, TimeSpan.Zero);
        var result = new IsolatedBonusVideoRunResult(
            IsolatedBonusVideoRunStatus.CooldownActive,
            "cooldown",
            BonusVideoFailureKind.NoAdOrCookies,
            now.AddSeconds(99.1));

        Assert.Equal(BonusVideoFailureKind.NoAdOrCookies, result.FailureKind);
        Assert.Equal(100, result.RemainingRetrySeconds(now));
    }

    [Fact]
    public void SafeVideoRequestLabel_RemovesQueryAndCredentials()
    {
        var label = BrowserSession.SafeVideoRequestLabel(
            "https://user:secret@imasdk.googleapis.com/video/path?token=private&account=123");

        Assert.Equal("imasdk.googleapis.com", label);
    }

    [Fact]
    public void SafeVideoFailureReason_OnlyKeepsNetworkErrorCode()
    {
        var reason = BrowserSession.SafeVideoFailureReason(
            "net::ERR_CONNECTION_RESET https://cdn.example/video?token=private");

        Assert.Equal("net::ERR_CONNECTION_RESET", reason);
    }
}
