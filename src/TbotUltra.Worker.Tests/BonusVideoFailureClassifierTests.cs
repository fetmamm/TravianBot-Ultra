using TbotUltra.Worker.Infrastructure;
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
    public void BonusVideoCooldownException_ReportsRemainingSeconds()
    {
        var now = new DateTimeOffset(2026, 7, 15, 20, 17, 21, TimeSpan.Zero);
        var exception = new BonusVideoCooldownException(now.AddSeconds(99.1), BonusVideoFailureKind.NoAdOrCookies);

        Assert.Equal(BonusVideoFailureKind.NoAdOrCookies, exception.Kind);
        Assert.Equal(100, exception.RemainingSeconds(now));
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
