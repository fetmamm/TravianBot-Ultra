using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationQueueItemFailurePort(MainWindow owner)
        : IAutomationQueueItemFailurePort
    {
        private readonly MainWindowAutomationQueueContext _queueContext = new(owner);

        public ValueTask<bool?> VerifySmithyMissingAsync(QueueItem item) =>
            new(owner.VerifySmithyMissingAsync(item));
        public bool MarkSucceeded(Guid itemId) => owner._botService.MarkQueueItemSucceeded(itemId);
        public bool DisableTroopsGroupForVillage(QueueItem item, out string blockedVillageName) =>
            owner.DisableTroopsGroupForQueueItemVillage(item, out blockedVillageName);
        public void SetTroopsBlockedState(string reasonKey, string reasonText) =>
            owner.SetTroopsBlockedState(reasonKey, reasonText);
        public void DisableTownHallForVillage(string villageKey, string? villageName)
        {
            void Apply()
            {
                var village = new VillageSettingsStore.VillageKeyInfo(
                    villageKey,
                    villageName ?? villageKey,
                    null,
                    null,
                    false);
                owner.PersistAutomationGroupEnabledForVillage(
                    village,
                    enabled: false,
                    QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration));
                TownHallCelebrationStateStore.Clear(
                    owner._projectRoot,
                    owner._accountStore.ActiveAccountName(),
                    villageKey);
                owner.InvalidateVillageOverviewTownHallCache();
                owner.RefreshAutomationLoopDashboardUi();
            }

            if (owner.Dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                owner.Dispatcher.Invoke(Apply);
            }
        }
        public ValueTask ApplyConstructionInlineWaitAsync(
            TimeSpan delay,
            string? humanizeVillageKey,
            TimeSpan? humanizeWait) =>
            new(owner.Dispatcher.InvokeAsync(() =>
                owner.ApplyConstructionInlineWait(delay, humanizeVillageKey, humanizeWait)).Task);
        public ValueTask ApplyHeroLowHpCooldownAsync(TimeSpan delay) =>
            new(owner.ApplyHeroLowHpCooldownUiAsync(delay));
        public void ApplyBreweryCelebrationDeferSignal(string? message, TimeSpan delay) =>
            owner.ApplyBreweryCelebrationDeferSignal(message, delay);
        public void ApplyTownHallCelebrationDeferSignal(
            QueueItem item,
            string? message,
            TimeSpan delay) =>
            owner.ApplyTownHallCelebrationDeferSignal(item, message, delay);
        public bool MarkDeferred(Guid itemId, TimeSpan delay) =>
            owner._botService.MarkQueueItemDeferred(itemId, delay);
        public string? GetVillageKey(QueueItem item) => owner.GetQueueItemVillageKey(item);
        public string? GetVillageName(QueueItem item) => GetQueueItemVillageName(item);
        public void ClearConstructionLoginFillForBlockedHead(QueueItem item, string source) =>
            owner.ClearConstructionLoginFillForBlockedHead(item, source);
        public AutomationConstructionRequirementContext GetConstructionRequirementContext(QueueItem item) =>
            _queueContext.GetConstructionRequirementContext(item);
        public bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload) =>
            owner.PatchDeferredQueuePayload(item, payload);
        public bool MarkPermanentlyFailed(Guid itemId) =>
            owner._botService.MarkQueueItemPermanentlyFailed(itemId);
        public void RaisePermanentFailureAlarm(QueueItem item, string message) =>
            owner.RaiseAlarmIfQueueItemPermanentlyFailed(item, message);
        public ValueTask RefreshVillageActivityIndicatorsAsync() =>
            new(owner.Dispatcher.InvokeAsync(owner.RefreshVillageActivityIndicatorsOnDashboard).Task);
        public string FormatServerTime(DateTimeOffset value) => owner.FormatQueueServerTime(value);
        public void RebindPendingTemplateStep(QueueItem item, int effectiveSlotId) =>
            owner.RebindPendingBuildingTemplateStep(item, effectiveSlotId);
        public ValueTask HandleStorageCapacityDependencyAsync(
            QueueItem item,
            Dictionary<string, string> payload) =>
            new(owner.TryHandleStorageCapacityDependencyAsync(item, payload));
        public ValueTask RefreshFarmListsAfterAutoSendAsync(QueueItem item, string message) =>
            new(owner.RefreshFarmListsUiAfterAutoSendIfNeededAsync(item, message));
        public ValueTask RefreshConstructionStatusAfterDeferAsync() =>
            new(owner.RefreshConstructionStatusAfterDeferAsync(
                owner._loopController.AcquireSessionScopeToken()));
        public ValueTask HandleCropShortageDeferAsync(QueueItem item) =>
            new(owner.HandleCropShortageDeferAsync(item));
        public ValueTask RefreshTroopTrainingAfterBuildAsync(QueueItem item) =>
            new(owner.RefreshTroopTrainingUiAfterBuildAsync(
                item,
                owner.LoadBotOptions(),
                owner._loopController.AcquireSessionScopeToken()));
        public bool UpdateDeferredPayload(Guid itemId, Dictionary<string, string> payload) =>
            owner._botService.UpdateDeferredQueueItem(itemId, payload);
        public bool MarkExecutionFailed(Guid itemId) =>
            owner._botService.MarkQueueItemExecutionFailed(itemId);
        public void HandleStorageDependencyFailed(QueueItem item, string message) =>
            owner.HandleStorageDependencyFailed(item, message);
        public string FormatException(Exception exception) => FormatExceptionForLog(exception);
        public void Log(string message) => owner.AppendLog(message);
    }
}
