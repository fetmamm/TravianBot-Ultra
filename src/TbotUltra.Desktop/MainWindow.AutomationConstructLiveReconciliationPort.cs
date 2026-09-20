using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationConstructLiveReconciliationPort(MainWindow owner)
        : IAutomationConstructLiveReconciliationPort
    {
        public void ReconcileLiveQueue(VillageStatus status) =>
            owner.ReconcilePendingBuildingQueueWithLiveStatus(status);
        public void RebindPendingUpgrades(QueueItem source, int liveSlotId) =>
            owner.RebindPendingBuildingUpgrades(source, liveSlotId);
        public bool MarkDeferred(Guid itemId, TimeSpan delay) =>
            owner._botService.MarkQueueItemDeferred(itemId, delay);
        public bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload) =>
            owner.PatchDeferredQueuePayload(item, payload);
        public void RebindPendingTemplateStep(QueueItem source, int liveSlotId) =>
            owner.RebindPendingBuildingTemplateStep(source, liveSlotId);
        public bool MarkSucceeded(Guid itemId) => owner._botService.MarkQueueItemSucceeded(itemId);
        public bool RemoveQueueItem(Guid itemId) => owner._botService.RemoveQueueItem(itemId);
        public IReadOnlyList<QueueItem> GetSameVillageQueueItems(QueueItem source)
        {
            var sameVillage = owner.BuildSameVillageQueueFilter(source);
            return owner._buildingsPanelService.GetQueueItems().Where(sameVillage).ToList();
        }
        public bool MarkPermanentlyFailed(Guid itemId) =>
            owner._botService.MarkQueueItemPermanentlyFailed(itemId);
        public void ForgetBuildingQueueCaches(QueueItem item) =>
            owner.ForgetBuildingQueueCachesForItem(item);
        public bool ApplyPendingQueueReconciliation(IReadOnlyList<QueuePayloadUpdate> updates) =>
            owner._botService.ApplyPendingQueueReconciliation([], updates);
        public string? GetVillageName(QueueItem item) =>
            NormalizeVillageName(GetQueueItemVillageName(item));
        public void RequestQueueUiRefresh() => owner.RequestQueueUiRefresh();
        public void Log(string message) => owner.AppendLog(message);
    }
}
