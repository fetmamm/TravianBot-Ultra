using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowContinuousIdlePacingPort(
        MainWindow owner,
        AutomationPassRuntime passRuntime)
        : IContinuousIdlePacingPort
    {
        public bool SessionAvailable =>
            !owner.IsSessionSleeping && owner._isLoggedIn && owner._browserSessionLikelyOpen;
        public bool StopRequested => owner._loopController.LoopStopRequested;
        public bool ImmediateWorkRequested => passRuntime.IsImmediateWorkRequested;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            new(Task.Delay(delay, cancellationToken));
        public IDisposable BeginBrowseActivity() =>
            owner._dashboardActivityTracker.Begin("Idle browsing");
        public ValueTask NavigateAsync(
            BotOptions options,
            string path,
            CancellationToken cancellationToken) =>
            new(owner._botService.NavigateToPageAndReadHtmlAsync(
                options,
                path,
                owner.AppendLog,
                owner._loopController.AcquireSessionScopeToken()));
        public void Log(string message) => owner.AppendLog(message);
    }
}
