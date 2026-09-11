using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SessionSleepBrowserShutdownSourceTests
{
    [Fact]
    public void ControlledSleep_DoesNotEnterSleepingStateWhenBrowserShutdownFails()
    {
        var methodBody = ReadMethod(
            "private async Task HandleSessionPacingSleepStartingAsync(bool manual = false)",
            "    private async Task<bool> RequestGracefulAutomationStopForSleepAsync()");

        var close = methodBody.IndexOf("await CloseBrowserForSleepAsync(operationId);", StringComparison.Ordinal);
        var beginSleep = methodBody.IndexOf("_sessionPacer.BeginSleep(manual);", StringComparison.Ordinal);
        var closeFailure = methodBody.IndexOf("session browser close failed", StringComparison.Ordinal);
        var returnAfterFailure = methodBody.IndexOf("return;", closeFailure, StringComparison.Ordinal);

        Assert.True(close >= 0 && beginSleep > close);
        Assert.True(closeFailure >= 0 && returnAfterFailure > closeFailure && returnAfterFailure < beginSleep);
    }

    [Fact]
    public void PlannedSleep_ClosesBrowserBeforeEnteringSleepingStateAndPropagatesCleanupFailure()
    {
        var methodBody = ReadMethod(
            "private async Task<bool> TryEnterPlannedSleepInsteadOfLoginAsync()",
            "    private async Task SafeSessionPacingInvokeAsync(Func<Task> action)");

        var close = methodBody.IndexOf("await CloseBrowserForSleepAsync(\"Planned sleep\");", StringComparison.Ordinal);
        var beginSleep = methodBody.IndexOf("_sessionPacer.BeginScheduledSleepNow()", StringComparison.Ordinal);

        Assert.True(close >= 0 && beginSleep > close);
        Assert.DoesNotContain("planned sleep browser close failed", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPacingCallbacks_AreMarshaledToDispatcher()
    {
        var methodBody = ReadMethod(
            "    private async Task SafeSessionPacingInvokeAsync(Func<Task> action)",
            "    private void SessionPacingRunNowButton_Click");

        Assert.Contains("Dispatcher.CheckAccess()", methodBody, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.InvokeAsync(action)", methodBody, StringComparison.Ordinal);
    }

    private static string ReadMethod(string startMarker, string endMarker)
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.SessionPacing.cs"));
        var methodStart = source.IndexOf(startMarker, StringComparison.Ordinal);
        var methodEnd = source.IndexOf(endMarker, methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        return source[methodStart..methodEnd];
    }
}
