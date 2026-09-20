using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SessionWakeLoginSourceTests
{
    [Fact]
    public void Lobby_unavailable_during_wake_remains_retry_status()
    {
        Assert.True(MainWindow.IsExpectedWakeLoginRetry(new InvalidOperationException(
            "Login through the Travian lobby did not reach the configured game world. Direct server login is disabled.")));
    }

    [Fact]
    public void Unexpected_wake_login_failure_remains_actionable()
    {
        Assert.False(MainWindow.IsExpectedWakeLoginRetry(
            new InvalidOperationException("Unexpected account mapping failure.")));
    }

    [Fact]
    public void Wake_login_reports_intermediate_failures_as_retry_status()
    {
        var pacingSource = ReadSource("MainWindow.SessionPacing.cs");
        var sessionSource = ReadSource("MainWindow.Session.cs");
        var loopSource = ReadSource("MainWindow.ContinuousLoop.cs");

        Assert.Contains("ExecuteLoginFlowAsync(retryFailureIsStatus: true)", pacingSource, StringComparison.Ordinal);
        Assert.Contains("if (retryFailureIsStatus && IsExpectedWakeLoginRetry(ex))", sessionSource, StringComparison.Ordinal);
        Assert.Contains("[{operationId}] RETRY", sessionSource, StringComparison.Ordinal);
        Assert.Contains("|| IsExpectedWakeLoginRetry(ex)", loopSource, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "TbotUltra.Desktop", fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
