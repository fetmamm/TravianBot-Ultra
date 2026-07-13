using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousLoopNetworkBackoffTests
{
    [Fact]
    public void ResolveWaitSeconds_NetworkBackoffIsNotShortenedByActionPacing()
    {
        var options = new BotOptions
        {
            ActionPacingEnabled = true,
            ActionPacingLoopMinSeconds = 4,
            ActionPacingLoopMaxSeconds = 25,
        };

        var seconds = MainWindow.ResolveContinuousLoopWaitSeconds(
            TimeSpan.FromSeconds(47),
            options,
            networkBackoff: true);

        Assert.Equal(47, seconds);
    }

    [Fact]
    public void IsTransientConnectionFailure_AcceptsNavigationAndUnknownPageState()
    {
        Assert.True(MainWindow.IsTransientConnectionFailure(
            new TransientNavigationException("Navigation timed out.")));
        Assert.True(MainWindow.IsTransientConnectionFailure(
            new InvalidOperationException("Not logged in. Current page state is 'unknown'.")));
        Assert.True(MainWindow.IsTransientConnectionFailure(
            new InvalidOperationException("Wrapped", new TransientNavigationException("Navigation timed out."))));
    }

    [Fact]
    public void IsTransientConnectionFailure_RejectsConfirmedLoggedOutState()
    {
        Assert.False(MainWindow.IsTransientConnectionFailure(
            new InvalidOperationException("Not logged in. Current page state is 'logged_out'.")));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(8, 10)]
    public void ResolveAutomaticProxyRecoveryRetryDelay_UsesBoundedBackoff(int attempt, int minutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(minutes),
            MainWindow.ResolveAutomaticProxyRecoveryRetryDelay(attempt));
    }
}
