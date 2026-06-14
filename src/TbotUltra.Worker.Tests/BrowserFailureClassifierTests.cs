using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserFailureClassifierTests
{
    [Fact]
    public void IsTargetCrash_MatchesNestedExceptionCaseInsensitively()
    {
        var exception = new InvalidOperationException(
            "Queue execution failed.",
            new Exception("PlaywrightException: TARGET CRASHED"));

        Assert.True(BrowserFailureClassifier.IsTargetCrash(exception));
    }

    [Fact]
    public void IsTargetCrash_RejectsOrdinaryTimeout()
    {
        var exception = new TimeoutException("Page navigation timed out.");

        Assert.False(BrowserFailureClassifier.IsTargetCrash(exception));
    }
}
