using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationQueueSelectionPort(
        MainWindow owner,
        AutomationQueueEligibility queueEligibility,
        AutomationPassRuntime passRuntime)
        : IAutomationQueueSelectionPort
    {
        public string? ActiveVillageKey => owner._activeWorkingVillageKey;
        public string? ActiveVillageName => owner._activeWorkingVillageName;
        public BotOptions LoadOptions() => owner.LoadBotOptions();
        public void RemoveDisabledUtilityItems(BotOptions options) =>
            owner.RemoveDisabledAutoCollectUtilityItems(options);
        public IReadOnlyList<QueueItem> GetQueueItems() => owner._botService.GetQueueItemsForDisplay();
        public string? GetVillageKey(QueueItem item) => owner.GetQueueItemVillageKey(item);
        public string? GetVillageName(QueueItem item) => GetQueueItemVillageName(item);
        public bool IsAllowedByAutomationSettings(QueueItem item) =>
            queueEligibility.IsAllowed(item);
        public bool IsUtilityEnabled(string taskName, BotOptions options) =>
            owner.IsAutoCollectUtilityTaskEnabledNow(taskName, options);
        public IReadOnlyList<QueueGroup> GetConsideredGroups() =>
            owner.GetContinuousLoopConsideredGroupsInOrder();
        public VillageBatchSnapshot SnapshotVillageBatch() =>
            passRuntime.SnapshotVillageBatch(owner._activeWorkingVillageKey);
        public QueueItem? SelectReadyConstruction(
            IReadOnlyList<QueueItem> villageItems,
            DateTimeOffset now,
            bool preview) => owner.SelectReadyConstructionForAutomationPass(villageItems, now, preview);
        public void CompleteUrgentPreemption() =>
            passRuntime.CompleteUrgentPreemption(owner._activeWorkingVillageKey);
        public void RecordUrgentPreemption(string? targetVillageKey) =>
            passRuntime.RecordUrgentPreemption(
                owner._activeWorkingVillageKey,
                targetVillageKey);
        public string FormatServerTime(DateTimeOffset value) => owner.FormatQueueServerTime(value);
        public void Log(string message) => owner.AppendLog(message);
        public void LogVerbose(string message, string key) => owner.AppendLoopPickVerbose(message, key);
    }
}
