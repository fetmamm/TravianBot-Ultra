using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowContinuousAutomationForecastPort(
        MainWindow owner,
        AutomationQueueEligibility queueEligibility,
        AutomationQueueSelectionCoordinator queueSelection)
        : IContinuousAutomationForecastPort
    {
        public IReadOnlyList<QueueItem> GetQueueItems() => owner.GetQueueSnapshotForUi();
        public string? GetVillageKey(QueueItem item) => owner.GetQueueItemVillageKey(item);
        public bool IsAllowedByAutomationSettings(QueueItem item) =>
            queueEligibility.IsAllowed(item);
        public TimeSpan? ResolveConstructionQueueDelay(QueueItem item, DateTimeOffset now)
        {
            var status = owner.ResolveBuildingStatusForQueueItem(item);
            return status is null
                ? null
                : ConstructionQueueState.ResolveQueueFullRetryDelay(
                    status,
                    owner._travianPlusActive,
                    item,
                    now);
        }
        public TimeSpan? ResolveSmartSleepConstructionQueueClearDelay(QueueItem item, DateTimeOffset now)
        {
            var status = owner.ResolveBuildingStatusForQueueItem(item);
            return ConstructionQueueState.ResolveSmartSleepQueueClearDelay(
                status,
                owner._travianPlusActive,
                item,
                now);
        }
        public TimeSpan? ResolveConstructPrerequisiteDelay(QueueItem item, DateTimeOffset now) =>
            owner.TryResolveConstructActivePrerequisiteDelay(item, now, out var dependencyDelay)
                ? dependencyDelay.Delay
                : null;
        public QueueItem? SelectPreview(
            DateTimeOffset evaluationTime,
            string? villageKeyFilter,
            IReadOnlyList<QueueItem> queueItems) => queueSelection.Select(
                preview: true,
                evaluationTimeUtc: evaluationTime,
                villageKeyFilter: villageKeyFilter,
                queueItemsOverride: queueItems);
        public bool HasKnownConstructionAvailability(QueueItem item, DateTimeOffset now) =>
            ConstructionQueueState.ResolveAvailabilityForItem(
                owner.ResolveBuildingStatusForQueueItem(item),
                owner._travianPlusActive,
                item,
                now) != ConstructionQueueAvailability.Unknown;
    }
}
