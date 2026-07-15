using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class AdventureVideoTimingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(44.999)]
    public void AttemptCannotAbortBeforeFortyFiveSeconds(double elapsedSeconds)
    {
        Assert.False(TravianClient.AdventureVideoAttemptMayAbort(elapsedSeconds));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(60)]
    public void AttemptMayAbortAfterFortyFiveSeconds(double elapsedSeconds)
    {
        Assert.True(TravianClient.AdventureVideoAttemptMayAbort(elapsedSeconds));
    }
}
