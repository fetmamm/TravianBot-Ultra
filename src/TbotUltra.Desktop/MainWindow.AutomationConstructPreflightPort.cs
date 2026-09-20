using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationConstructPreflightPort(MainWindow owner)
        : IAutomationConstructPreflightPort
    {
        public string? GetTargetVillageName(QueueItem item) =>
            NormalizeVillageName(GetQueueItemVillageName(item));
        public string? GetTargetVillageUrl(QueueItem item) =>
            GetQueueItemPayloadValue(item, BotOptionPayloadKeys.TargetVillageUrl);
        public string? GetTargetVillageKey(QueueItem item) => owner.GetQueueItemVillageKey(item);
        public ValueTask<VillageStatus> ReadLiveVillageStatusAsync(
            BotOptions options,
            string? villageName,
            string? villageUrl,
            CancellationToken cancellationToken) =>
            new(owner._botService.ReadVillageStatusAsync(
                options,
                owner.AppendLog,
                villageName,
                villageUrl,
                cancellationToken));
        public void ApplyLiveVillageStatus(VillageStatus status, string? villageName) =>
            owner.Dispatcher.Invoke(() =>
            {
                owner.CacheVillageStatus(status, villageName);
                owner.ReconcilePendingBuildingQueueWithLiveStatus(status);
            });
        public VillageStatus? GetCachedBuildingStatus(QueueItem item) =>
            owner.ResolveBuildingStatusForQueueItem(item);
        public bool? TravianPlusActive => owner._travianPlusActive;
        public void ClearLoginFillForFullSlots(
            VillageStatus status,
            string? villageKey,
            string source) =>
            owner.ClearConstructionLoginFillForFullSlots(status, villageKey, source: source);
        public bool MarkDeferred(Guid itemId, TimeSpan delay) =>
            owner._botService.MarkQueueItemDeferred(itemId, delay);
        public bool PatchDeferred(
            Guid itemId,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyCollection<string> keysToRemove,
            TimeSpan delay) =>
            owner._botService.PatchDeferredQueueItem(itemId, values, keysToRemove, delay);
        public string? GetVillageName(QueueItem item) =>
            NormalizeVillageName(GetQueueItemVillageName(item));
        public string FormatServerTime(DateTimeOffset value) => owner.FormatQueueServerTime(value);
        public ValueTask RefreshVillageActivityIndicatorsAsync() =>
            new(owner.Dispatcher.InvokeAsync(owner.RefreshVillageActivityIndicatorsOnDashboard).Task);
        public void Log(string message) => owner.AppendLog(message);
    }
}
