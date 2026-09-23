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

    [Theory]
    [InlineData("[resource-refresh] FAIL Timeout 20000ms exceeded.")]
    [InlineData("Background resource refresh skipped: Timeout 20000ms exceeded.")]
    public void SingleBackgroundResourceRefreshTimeout_IsNotAlarm(string message)
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
    public void PreSleepFillHoldStarted_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[pre-sleep-fill] hold started: tracked=1, pendingDispatch=1, running=0, dispatchTimeout=30s, holdLimit=3m."));
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

    [Theory]
    [InlineData("[ensure-logged-in] browser network error page detected url='chrome-error://chromewebdata/'.")]
    [InlineData("[nav] GOTO start target='https://lobby.legends.travian.com/account' from='chrome-error://chromewebdata/' pages=1")]
    [InlineData("[lobby-login] transient lobby attempt 1/3 failed: net::ERR_TIMED_OUT")]
    [InlineData("[lobby-login] transient lobby attempt 2/3 failed: net::ERR_TIMED_OUT")]
    public void IntermediateNetworkRecoveryMessages_AreNotAlarms(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Theory]
    [InlineData("[upgrade_all_resources_to_level FAILED] after 12.6s: InvalidOperationException: navigate to /dorf1.php failed after 3 attempts: net::ERR_NAME_NOT_RESOLVED")]
    [InlineData("[queue] FAIL id=123 task='upgrade_all_resources_to_level' after 12.8s: InvalidOperationException: net::ERR_NETWORK_CHANGED")]
    [InlineData("[LOOP 13] FAIL 12.8s | InvalidOperationException: net::ERR_CONNECTION_TIMED_OUT")]
    public void NetworkOutageFollowOnDiagnostics_AreNotSeparateAlarms(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Fact]
    public void RecoveredNavigationTimeout_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[nav] RELOAD timeout recovered: expected page is usable despite missing navigation event."));
    }

    [Fact]
    public void ExhaustedLobbyRecovery_RemainsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "[lobby-login] transient lobby attempt 3/3 failed: net::ERR_CONNECTION_TIMED_OUT"));
    }

    [Fact]
    public void CompletedVillageMembershipVerification_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[village-membership] profile verification complete: villages=8 removedConfirmed=true."));
    }

    [Theory]
    [InlineData("[village-membership] live sidebar differs from the verified UI list (2/2); blocking automation until profile verification completes.")]
    [InlineData("[village-membership] profile verification returned 2 village(s).")]
    public void SuccessfulVillageMembershipVerificationProgress_IsNotAlarm(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Fact]
    public void RetryingWakeLogin_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[pacing] wake login failed (attempt 4) — retrying in 10 min."));
    }

    [Theory]
    [InlineData("[upgrade_all_resources_to_level FAILED] after 179.7s: InvalidOperationException: Upgrade analysis failed for slot 8: Navigation to 'https://example.test/build.php?id=8' timed out after safe retries.")]
    [InlineData("Could not capture diagnostics for 'upgrade-slot-8-exception': Timeout 20000ms exceeded.")]
    public void DeferredSafeNavigationDiagnostics_AreNotAlarms(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Fact]
    public void EmptyVillageMembershipVerification_RemainsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "[village-membership] profile verification returned no villages; preserving the cached list."));
    }
}
