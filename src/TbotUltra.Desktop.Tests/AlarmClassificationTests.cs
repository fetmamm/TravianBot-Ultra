using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AlarmClassificationTests
{
    [Fact]
    public void LobbyWorldResolutionPendingSave_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[lobby-login] manually selected owned world resolved to 'cw.x5.international.travian.com'; account correction pending authenticated game verification."));
    }

    [Fact]
    public void ExpectedBonusVideoFallback_IsWarningNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[construct-faster] WARNING: video unavailable after timeout; building normally."));
    }

    [Theory]
    [InlineData("[construct-faster] video attempt 1/2 ended before normal completion: Timeout 20000ms exceeded. Verifying on fresh dorf2 before fallback.")]
    [InlineData("[construct-faster] skipping immediate video retry after Timeout; building normally without changing route.")]
    [InlineData("[construct-faster] skipped video — shared account/proxy cooldown active after video timeout; building normally.")]
    [InlineData("[browser-video] isolated bonus-video browser closed reason='action failed with video timeout'.")]
    public void ExpectedBonusVideoDegradation_IsWarningNotAlarm(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Theory]
    [InlineData("[resource-refresh] FAIL Unable to retrieve content because the page is navigating and changing the content.")]
    [InlineData("Background resource refresh skipped: Unable to retrieve content because the page is navigating and changing the content.")]
    public void ResourceRefreshNavigationRace_IsNotAlarm(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
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

    [Fact]
    public void TransientUpgradeAnalysisRetry_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "Upgrade analysis for slot 10 hit transient execution-context error on attempt 1/3. Retrying..."));
    }

    [Fact]
    public void ExhaustedUpgradeAnalysisRetry_RemainsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "Upgrade analysis failed for slot 10: exhausted retries."));
    }

    [Fact]
    public void CompletedVillageMembershipVerification_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[village-membership] profile verification complete: villages=8 removedConfirmed=true."));
    }

    [Fact]
    public void EmptyVillageMembershipVerification_RemainsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "[village-membership] profile verification returned no villages; preserving the cached list."));
    }
}
