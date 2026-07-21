using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListTimerTests
{
    [Fact]
    public void ResolveFarmListRemaining_WithoutReadableTimer_ReturnsNoTimer()
    {
        var result = TravianClient.ResolveFarmListRemaining(string.Empty);

        Assert.Null(result.RemainingSeconds);
        Assert.False(result.IsEstimated);
    }

    [Fact]
    public void ResolveFarmListRemaining_ReadableTimer_PreservesExactValue()
    {
        var result = TravianClient.ResolveFarmListRemaining("01:30");

        Assert.Equal(90, result.RemainingSeconds);
        Assert.False(result.IsEstimated);
    }
}
