using System.Diagnostics;
using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowSessionSleepLifecyclePort(MainWindow owner) : ISessionSleepLifecyclePort
    {
        public SessionSleepHostState ReadState() => new(
            owner.IsFreezeActive,
            owner._loopController.HasActiveOperation,
            owner._isLoggedIn,
            owner.IsContinuousLoopRunning(),
            owner._autoQueueRunning,
            owner._automationDesk?.LoginVillageStatusRoundPending == true,
            owner._loginInProgress,
            owner._accountSwitchInProgress,
            owner._shutdownInProgress || owner._shutdownCompleted || owner._loopController.IsClosing);

        public int ReadVillageRoundSleepExtensionMinutes() =>
            owner.LoadBotOptions().VillageRoundSleepExtensionMinutes;

        public int ReadSmartSleepMinimumOpportunityMinutes() =>
            owner._smartSleepSettings.MinimumOpportunityMinutes;

        public ValueTask<SessionPreSleepFillResult> WaitForPreSleepFillAsync() =>
            new(owner.WaitBrieflyForPreSleepFillItemsAsync());

        public void RequestAutomationStop(AutomationStopMode mode) => owner.RequestAutomationStop(mode);

        public void CancelActiveOperation() => owner._loopController.CancelOperation();

        public void PrepareOfflineForSleep()
        {
            owner._isLoggedIn = false;
            owner._browserSessionLikelyOpen = false;
            owner._inboxAutoEnabled = false;
        }

        public Task StopAllAutomationAsync() => owner.StopAllAutomationAndWaitAsync();

        public async Task CloseBrowserForSleepAsync(string operationName, bool showBusyState)
        {
            if (!showBusyState)
            {
                await owner.CloseBrowserForSleepAsync(operationName);
                return;
            }

            var operationId = owner.BeginOperation(operationName);
            var operationSw = Stopwatch.StartNew();
            owner.ToggleUiBusy(true);
            try
            {
                await owner.CloseBrowserForSleepAsync(operationId);
                owner.CompleteOperation(operationId, operationSw, $"{operationName} browser close completed.");
            }
            finally
            {
                owner.ToggleUiBusy(false);
            }
        }

        public void ActivatePendingProxyAtSleep()
        {
            if (owner._pendingProxyChangeAtSleep is not { } pendingProxy)
            {
                return;
            }

            owner._accountStore.SaveAccount(pendingProxy, setActive: false);
            owner._pendingProxyChangeAtSleep = null;
            owner.AppendLog("[proxy-change] pending proxy activated at session sleep; next wake will start a fresh browser.");
        }

        public void ReloadPacerConfiguration() => owner.ConfigureSessionPacerFromConfig();

        public Task ApplyProxyPlanForWakeAsync(DateTimeOffset wakeAt) => owner.ApplyProxyPlanForWakeAsync(wakeAt);

        public void UpdateSessionActivity() => owner.UpdateSessionActivityState(forcePersist: true);

        public void UpdateUi() => owner.UpdateSessionPacingUi();

        public async Task<bool> LoginForWakeAsync()
        {
            await owner.ExecuteLoginFlowAsync(retryFailureIsStatus: true);
            return owner._isLoggedIn;
        }

        public bool ConsumeForcedVillageRoundOnWake() =>
            owner._automationDesk.ConsumeForceVillageStatusRoundOnWake();

        public void ForceVillageRound() => owner._automationDesk.RequestForcedVillageStatusRound();

        public void ResumeContinuousLoop() => owner.StartContinuousLoopRunner();

        public void ResumeAutoQueue() => _ = owner.TriggerQueueAutoRunAsync();

        public void Log(string message) => owner.AppendLog(message);
    }
}
