using TbotUltra.Desktop.Services.Logging;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LogPresentationPolicyTests
{
    [Theory]
    [InlineData("[browser-trace:verbose] run=abc event=read result=success")]
    [InlineData("[pacing:verbose] action delay completed")]
    [InlineData("[scan:verbose] candidate ignored")]
    public void HighVolumeDiagnostics_AvoidUiQueueWhenLogsAreHidden(string message)
    {
        Assert.False(LogPresentationPolicy.ShouldQueueForUi(message, logsVisible: false));
        Assert.True(LogPresentationPolicy.ShouldQueueForUi(message, logsVisible: true));
    }

    [Theory]
    [InlineData("ALARM: state-changing click timed out")]
    [InlineData("[license] Automation lease started")]
    [InlineData("Resource slot 1 level increased from 4 to 5")]
    public void OperationalMessages_AlwaysReachUi(string message)
    {
        Assert.True(LogPresentationPolicy.ShouldQueueForUi(message, logsVisible: false));
    }

    [Fact]
    public void CapturedTraceVolume_DoesNotCreateHiddenUiRows()
    {
        var queued = Enumerable.Range(0, 17_044)
            .Count(index => LogPresentationPolicy.ShouldQueueForUi(
                $"[browser-trace:verbose] run=abc seq={index} event=read result=success",
                logsVisible: false));

        Assert.Equal(0, queued);
    }
}
