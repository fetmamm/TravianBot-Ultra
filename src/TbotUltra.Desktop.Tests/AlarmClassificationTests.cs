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
}
