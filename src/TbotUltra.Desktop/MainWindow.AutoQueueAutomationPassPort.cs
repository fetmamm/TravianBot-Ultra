using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutoQueueAutomationPassPort(
        MainWindow owner,
        AutomationActionExecutor actionExecutor)
        : IAutoQueueAutomationPassPort
    {
        public long RunLogId => owner._automationPassRuntime.AutoQueueRunLogId;

        public bool PrioritizeDeadlineWorkOnWake
        {
            get => owner._automationPassRuntime.PrioritizeDeadlineWorkOnWake;
            set => owner._automationPassRuntime.PrioritizeDeadlineWorkOnWake = value;
        }

        public IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups =>
            owner._automationPassRuntime.SmartSleepDeadlineGroups;

        public bool HasPendingLoginRound => owner._continuousVillageStatusRound.LoginRoundPending;

        public QueueItem? SelectReadyPriorityQueueItem(BotOptions options) =>
            owner.SelectUrgentQueueItemForVillageStatusSweep(
                options,
                new HashSet<Guid>(),
                explicitPriorityOnly: true);

        public ValueTask RunPendingLoginRoundAsync(BotOptions options, CancellationToken cancellationToken) =>
            owner._continuousVillageStatusRound.RunIfDueAsync(
                AutomationExecutionOptions.WithoutImplicitVillageTarget(options), cancellationToken);

        public BotOptions LoadOptionsWithSelectedVillage() =>
            owner.ApplySelectedVillageToOptions(owner.LoadBotOptions());

        public ValueTask HonorPendingVillageSwitchAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.HonorPendingVillageSwitchAsync(options, cancellationToken));

        public QueueItem? SelectNextQueueItem() => owner._automationQueueSelection.Select();

        public IReadOnlyList<QueueItem> GetQueueItems() => owner._botService.GetQueueItemsForDisplay();

        public IReadOnlyDictionary<Guid, DateTimeOffset> GetSmartSleepQueueDeadlineOverrides(
            IReadOnlyList<QueueItem> items,
            DateTimeOffset now) => owner.ResolveSmartSleepQueueDeadlineOverrides(items, now);

        public bool IsAllowedByAutomationSettings(QueueItem item) =>
            owner.IsQueueItemAllowedByAutomationSettings(item);

        public bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc) =>
            owner.TryRequestSmartSleep(trustedDeadlineUtc);

        public void Log(string message) => owner.AppendLog(message);

        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken) =>
            actionExecutor.ExecuteAsync(AutomationRunMode.AutoQueue, action, cancellationToken);
    }
}
