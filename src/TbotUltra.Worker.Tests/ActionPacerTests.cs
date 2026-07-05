using System.Diagnostics;
using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ActionPacerTests
{
    [Fact]
    public async Task DelayAsync_WhenDisabled_ReturnsWithoutWaiting()
    {
        var pacer = new ActionPacer(enabled: false);
        var stopwatch = Stopwatch.StartNew();

        await pacer.DelayAsync(10, 20, CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"disabled pacer waited {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DelayMillisecondsAsync_NonPositiveMax_DoesNotWait()
    {
        var pacer = new ActionPacer(enabled: true);
        var stopwatch = Stopwatch.StartNew();

        await pacer.DelayMillisecondsAsync(0, 0, CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"pacer waited {stopwatch.ElapsedMilliseconds}ms for a zero delay");
    }

    [Fact]
    public async Task DelayMillisecondsAsync_MinGreaterThanMax_DoesNotThrow()
    {
        var pacer = new ActionPacer(enabled: true);

        // max is clamped up to min internally so Random.Next(min, max + 1) stays a valid range.
        await pacer.DelayMillisecondsAsync(5, 1, CancellationToken.None);
    }

    [Fact]
    public async Task FromOptions_Disabled_IsNoOp_EvenForLargeDelay()
    {
        var pacer = ActionPacer.FromOptions(new BotOptions { ActionPacingEnabled = false });
        var stopwatch = Stopwatch.StartNew();

        await pacer.DelayMillisecondsAsync(5000, 5000, CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"disabled pacer waited {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task FromOptions_Enabled_AppliesDelay()
    {
        var pacer = ActionPacer.FromOptions(new BotOptions { ActionPacingEnabled = true });
        var stopwatch = Stopwatch.StartNew();

        await pacer.DelayMillisecondsAsync(50, 50, CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds >= 25, $"enabled pacer only waited {stopwatch.ElapsedMilliseconds}ms");
    }
}
