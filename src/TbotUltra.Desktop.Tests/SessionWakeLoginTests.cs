using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SessionWakeLoginTests
{
    [Fact]
    public void LobbyUnavailableDuringWake_RemainsRetryStatus()
    {
        Assert.True(MainWindow.IsExpectedWakeLoginRetry(new InvalidOperationException(
            "Login through the Travian lobby did not reach the configured game world. Direct server login is disabled.")));
    }

    [Fact]
    public void UnexpectedWakeLoginFailure_RemainsActionable()
    {
        Assert.False(MainWindow.IsExpectedWakeLoginRetry(
            new InvalidOperationException("Unexpected account mapping failure.")));
    }
}
