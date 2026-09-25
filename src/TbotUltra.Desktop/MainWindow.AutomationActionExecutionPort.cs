using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationActionExecutionPort(
        MainWindow owner,
        AutomationQueueItemLifecycle queueItemLifecycle,
        AutomationPassRuntime passRuntime)
        : IAutomationActionExecutionPort
    {
        public long ContinuousPassId => passRuntime.CurrentContinuousPassId;

        public long AutoQueueRunLogId => passRuntime.AutoQueueRunLogId;

        public bool LoopStopRequested => owner._loopController.LoopStopRequested;

        public bool QueueStopRequested => owner._loopController.QueueStopRequested;

        public QueueItem? FindQueueItem(Guid id) => owner._botService
            .GetQueueItemsForDisplay()
            .FirstOrDefault(item => item.Id == id);

        public BotOptions LoadOptions() => owner.LoadBotOptions();

        public ValueTask EnsureChromiumInstalledAsync() =>
            new(owner.EnsureChromiumInstalledAsync());

        public void RecordVillageBatchAttempt(QueueItem item, string source) =>
            owner.RecordVillageBatchAttempt(item, source);

        public ValueTask<bool> ExecuteQueueItemAsync(
            QueueItem item,
            BotOptions options,
            AutomationRunMode mode,
            string logPrefix,
            CancellationToken cancellationToken) =>
            queueItemLifecycle.ExecuteAsync(item, options, logPrefix, mode, cancellationToken);

        public void MarkContinuousBrowserActivity(BotOptions options) =>
            owner.MarkContinuousBrowserActivity(options);

        public ValueTask ApplyPostTaskCooldownAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.ApplyPostTaskCooldownAsync(item, options, cancellationToken));

        public void Log(string message) => owner.AppendLog(message);
    }
}
