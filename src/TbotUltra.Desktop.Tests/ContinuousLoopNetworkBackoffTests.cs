using TbotUltra.Core.Configuration;
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
}
