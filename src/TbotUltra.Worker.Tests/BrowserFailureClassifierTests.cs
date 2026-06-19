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

    [Theory]
    [InlineData("Target page, context or browser has been closed")]
    [InlineData("Target closed")]
    [InlineData("Browser has been closed")]
    [InlineData("Page has been closed")]
    [InlineData("The page is closed")]
    [InlineData("Cannot navigate to closed page")]
    public void IsTargetCrash_MatchesFatalDisconnectMessages(string message)
    {
        var exception = new Exception($"PlaywrightException: {message}");

        Assert.True(BrowserFailureClassifier.IsTargetCrash(exception));
    }

    [Fact]
    public void IsTargetCrash_MatchesFatalDisconnectOnInnerException()
    {
        var exception = new InvalidOperationException(
            "Queue execution failed.",
            new Exception("Target page, context or browser has been closed"));

        Assert.True(BrowserFailureClassifier.IsTargetCrash(exception));
    }

    [Fact]
    public void IsTargetCrash_RejectsTransientExecutionContextDestroyed()
    {
        // This is a harmless navigation race the worker retries — it must not tear down the session.
        var exception = new Exception(
            "Execution context was destroyed, most likely because of a navigation.");

        Assert.False(BrowserFailureClassifier.IsTargetCrash(exception));
    }
}
