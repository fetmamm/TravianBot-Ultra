using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListTimerTests
{
    [Fact]
    public void ResolveFarmListRemaining_DisabledWithoutReadableTimer_UsesOneMinuteEstimate()
    {
        var result = TravianClient.ResolveFarmListRemaining(string.Empty, disabled: true);

        Assert.Equal(60, result.RemainingSeconds);
        Assert.True(result.IsEstimated);
    }

    [Fact]
    public void ResolveFarmListRemaining_ReadableTimer_PreservesExactValue()
    {
        var result = TravianClient.ResolveFarmListRemaining("01:30", disabled: true);

        Assert.Equal(90, result.RemainingSeconds);
        Assert.False(result.IsEstimated);
    }
}
