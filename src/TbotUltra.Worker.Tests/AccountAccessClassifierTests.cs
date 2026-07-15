using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class AccountAccessClassifierTests
{
    [Theory]
    [InlineData("https://example.test/banned", "", false, AccountAccessState.Restricted)]
    [InlineData("https://example.test/dorf1.php", "Your account has been banned", false, AccountAccessState.Restricted)]
    [InlineData("https://example.test/challenge", "", false, AccountAccessState.Challenge)]
    [InlineData("https://example.test/login.php", "", true, AccountAccessState.Challenge)]
    public void ClassifyExplicit_DetectsRestrictionAndChallengeFixtures(
        string url,
        string text,
        bool captcha,
        AccountAccessState expected)
    {
        Assert.Equal(expected, AccountAccessClassifier.ClassifyExplicit(url, text, captcha));
    }

    [Fact]
    public void RegisterVerifiedState_StopsOnThirdConsecutiveUnknownOnly()
    {
        var first = AccountAccessClassifier.RegisterVerifiedState(0, AccountAccessState.Unknown);
        var second = AccountAccessClassifier.RegisterVerifiedState(first.ConsecutiveUnknown, AccountAccessState.Unknown);
        var reset = AccountAccessClassifier.RegisterVerifiedState(second.ConsecutiveUnknown, AccountAccessState.Unavailable);
        var thirdSequence = AccountAccessClassifier.RegisterVerifiedState(2, AccountAccessState.Unknown);

        Assert.False(first.Stop);
        Assert.False(second.Stop);
        Assert.Equal(0, reset.ConsecutiveUnknown);
        Assert.False(reset.Stop);
        Assert.True(thirdSequence.Stop);
    }
}
