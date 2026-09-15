using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowFarmListsAutomationAdapter(MainWindow owner) : IFarmListsAutomationAdapter
    {
        public bool ContinuousLoopRunning => owner.IsContinuousLoopRunning();
        public bool StartContinuousAfterQueueStop => owner._startContinuousLoopAfterQueueStop;
        public bool AutoQueueRunning => owner._autoQueueRunning;
        public bool UiBusy => owner._uiBusy;
        public bool SessionAvailable => !owner._loopController.IsClosing
            && owner._isLoggedIn
            && !owner.IsSessionSleeping
            && !owner.IsFreezeActive;

        public void ClearPendingRestarts()
        {
            owner._startContinuousLoopAfterQueueStop = false;
            owner._restartContinuousLoopAfterStop = false;
        }

        public void RequestStopAfterCurrentAction()
            => owner.RequestAutomationStop(AutomationStopMode.AfterCurrentAction);

        public void UpdateExecutionIndicator() => owner.UpdateExecutionStateIndicator();

        public void StartContinuousLoop() => owner.StartContinuousLoopRunner();

        public Task StartAutoQueueAsync() => owner.TriggerQueueAutoRunAsync();
    }
}
