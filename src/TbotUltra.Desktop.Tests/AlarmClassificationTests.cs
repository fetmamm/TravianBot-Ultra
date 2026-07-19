using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AlarmClassificationTests
{
    [Fact]
    public void ExpectedBonusVideoFallback_IsWarningNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[construct-faster] WARNING: video unavailable after timeout; building normally."));
    }

    [Fact]
    public void ProductionBonusInspectionFallback_IsWarningNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[production-bonus] WARNING: inspection unavailable: Timeout while reading bonus dialog."));
    }

    [Fact]
    public void ExplicitAccountHold_IsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "ALARM: Automation stopped for account 'test'. Manual review is required."));
    }

    [Theory]
    [InlineData("[browser-session] active browser shutdown invalidated session generation 12.")]
    [InlineData("Chromium warmup started.")]
    [InlineData("Chromium warmup completed in 1.0s.")]
    [InlineData("[browser-click] Playwright click skipped candidate 1/1 for 'button.collect': Timeout 3000ms exceeded.")]
    public void ExpectedBrowserLifecycleMessages_AreNotAlarms(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }
}
