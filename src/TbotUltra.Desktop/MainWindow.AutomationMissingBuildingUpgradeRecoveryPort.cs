using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationMissingBuildingUpgradeRecoveryPort(MainWindow owner)
        : IAutomationMissingBuildingUpgradeRecoveryPort
    {
        public string? GetVillageName(QueueItem item) =>
            NormalizeVillageName(GetQueueItemVillageName(item));

        public ValueTask<VillageStatus> ReadLiveVillageStatusAsync(
            BotOptions options,
            QueueItem item,
            CancellationToken cancellationToken) =>
            new(owner._botService.ReadVillageStatusAsync(
                options,
                owner.AppendLog,
                GetVillageName(item),
                GetQueueItemPayloadValue(item, BotOptionPayloadKeys.TargetVillageUrl),
                cancellationToken));

        public void ApplyLiveVillageStatus(VillageStatus status, string? villageName) =>
            owner.Dispatcher.Invoke(() =>
            {
                owner.CacheVillageStatus(status, villageName);
                owner.ReconcilePendingBuildingQueueWithLiveStatus(status);
            });

        public void ReconcileLiveQueue(VillageStatus status) =>
            owner.ReconcilePendingBuildingQueueWithLiveStatus(status);

        public bool MarkDeferred(Guid itemId, TimeSpan delay) =>
            owner._botService.MarkQueueItemDeferred(itemId, delay);

        public bool MarkSucceeded(Guid itemId) => owner._botService.MarkQueueItemSucceeded(itemId);

        public bool RemoveQueueItem(Guid itemId) => owner._botService.RemoveQueueItem(itemId);

        public bool UpdatePendingQueueItem(
            Guid itemId,
            Dictionary<string, string> payload,
            int? priority,
            TimeSpan delay) =>
            owner._botService.UpdatePendingQueueItem(itemId, payload, priority, delay);

        public IReadOnlyList<QueueItem> GetQueueItems() =>
            owner._botService.GetQueueItemsForDisplay();

        public bool IsSameVillageOrGlobal(QueueItem source, QueueItem candidate) =>
            owner.BuildSameVillageQueueFilter(source)(candidate);

        public QueueItem Enqueue(
            string taskName,
            Dictionary<string, string> payload,
            int priority,
            int maxRetries) =>
            owner._botService.Enqueue(taskName, payload, priority, maxRetries);

        public void RequestQueueUiRefresh(Guid? selectedItemId = null) =>
            owner.RequestQueueUiRefresh(selectedItemId);

        public ValueTask RefreshVillageActivityIndicatorsAsync() =>
            new(owner.Dispatcher.InvokeAsync(owner.RefreshVillageActivityIndicatorsOnDashboard).Task);

        public void Log(string message) => owner.AppendLog(message);
    }
}
