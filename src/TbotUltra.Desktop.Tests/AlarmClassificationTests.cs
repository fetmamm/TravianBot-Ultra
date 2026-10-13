using Xunit;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop.Tests;

public sealed class AlarmClassificationTests
{
    [Theory]
    [InlineData("[slot-nav] slot 13 candidate 1/2 was not actionable: Timeout 3000ms exceeded.")]
    [InlineData("[slot-nav] slot 40 candidate 2/2 was not actionable: Timeout 3000ms exceeded.")]
    public void FailedOverviewClickCandidate_IsDiagnosticNotAlarm(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Fact]
    public void VerifiedSlotUrlFallback_IsListedButAutoAcknowledged()
    {
        const string message = "ALARM: [slot-nav] Overview click unavailable for visible slot 40 on Dorf2; verified URL fallback succeeded.";

        Assert.True(MainWindow.IsAlarmMessage(message));
        Assert.True(MainWindow.IsAutoAcknowledgedAlarmMessage(message));
        Assert.False(MainWindow.IsAutoAcknowledgedAlarmMessage(
            "ALARM: [slot-nav] URL fallback failed for visible slot 40 on Dorf2."));

        var alarms = new AlarmsViewModel();
        alarms.RecordAlarm(message, "account|slot-40", MainWindow.IsAutoAcknowledgedAlarmMessage(message), DateTimeOffset.UtcNow);
        Assert.True(Assert.Single(alarms.Entries).IsAcknowledged);
        Assert.Equal(0, alarms.UnacknowledgedCount);
    }

    [Fact]
    public void OneWorkerFailureHasSameAlarmSignatureInQueueAndLoopDiagnostics()
    {
        const string context = "[context account='account' task='upgrade_building_to_level' village='ELS' xy='-121|82']";
        var worker = $"[upgrade_building_to_level FAILED] after 65,3s: TimeoutException: Page did not reach a ready state. {context}";
        var queue = $"[queue] FAIL id=abc task='upgrade_building_to_level' village='ELS' after 65,4s: TimeoutException: Page did not reach a ready state. {context}";
        var loop = $"[LOOP 134] FAIL 65,4s | TimeoutException: Page did not reach a ready state. {context}";

        Assert.Equal(MainWindow.AlarmIncidentSignature(worker), MainWindow.AlarmIncidentSignature(queue));
        Assert.Equal(MainWindow.AlarmIncidentSignature(worker), MainWindow.AlarmIncidentSignature(loop));
        Assert.True(MainWindow.IsFollowOnFailureDiagnostic(queue));
        Assert.True(MainWindow.IsFollowOnFailureDiagnostic(loop));
        Assert.False(MainWindow.IsFollowOnFailureDiagnostic(worker));
    }

    [Theory]
    [InlineData("[farm-list] send: 'Rainbow' marked sent (response=success+error; Travian processed the Start request).")]
    [InlineData("[farm-list] start-all: 'Steppes' marked sent (response=error; Travian processed the Start request).")]
    public void ConfirmedFarmListResponse_IsNotAlarm(string message)
    {
        Assert.False(MainWindow.IsAlarmMessage(message));
    }

    [Fact]
    public void MissingFarmListResponse_RemainsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "[farm-list] send: 'Rainbow' (lid 3639) showed no success/error response within 15 seconds after Start; not marking it sent."));
    }

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
    public void ExhaustedProductionBonusActivation_IsAlarm()
    {
        Assert.True(MainWindow.IsAlarmMessage(
            "[production-bonus] ALARM: activation was not confirmed for clay after at most 2 attempt(s) per resource; normal automation continues."));
    }

    [Fact]
    public void IntermediateProductionBonusFailure_IsNotSeparateAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[production-bonus:verbose] clay: activation not confirmed after attempt 1/2 (video timeout); continuing the current batch."));
    }

    [Fact]
    public void CapitalProfileDiagnostic_IsNotSeparateAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[capital:verbose] profile check incomplete: expected exactly one capital row, found 0."));
        Assert.True(MainWindow.IsAlarmMessage(
            "[OP0003] FAIL 6,7s | InvalidOperationException: Player profile did not identify exactly one capital village (found 0)."));
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

    [Fact]
    public void ActiveStorageMasterPlanVerification_IsNotAlarm()
    {
        Assert.False(MainWindow.IsAlarmMessage(
            "[storage-master-plan] Treasury slot 33 verification: active. [context account='account' task='upgrade_building_to_level' village='GAPP' xy='-122|83']"));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unknown")]
    public void UnsuccessfulStorageMasterPlanVerification_RemainsAlarm(string status)
    {
        Assert.True(MainWindow.IsAlarmMessage($"[storage-master-plan] Treasury slot 33 verification: {status}."));
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
