using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountSwitchPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void HasLiveBrowserSession_RequiresLoginAndBrowser(
        bool isLoggedIn,
        bool browserSessionOpen,
        bool expected)
    {
        Assert.Equal(expected, AccountSwitchPolicy.HasLiveBrowserSession(isLoggedIn, browserSessionOpen));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, false, false)]
    public void RequiresConfirmation_IncludesSleepingSessions(
        bool isLoggedIn,
        bool browserSessionOpen,
        bool isSessionSleeping,
        bool expected)
    {
        Assert.Equal(
            expected,
            AccountSwitchPolicy.RequiresConfirmation(isLoggedIn, browserSessionOpen, isSessionSleeping));
    }
}
