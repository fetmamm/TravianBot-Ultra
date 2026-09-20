using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationQueueItemSuccessPort(MainWindow owner)
        : IAutomationQueueItemSuccessPort
    {
        public bool MarkSucceeded(Guid itemId) => owner._botService.MarkQueueItemSucceeded(itemId);
        public bool RemoveQueueItem(Guid itemId) => owner._botService.RemoveQueueItem(itemId);
        public void RebindPendingBuildingUpgrades(QueueItem item, int liveSlotId) =>
            owner.RebindPendingBuildingUpgrades(item, liveSlotId);
        public void RebindPendingBuildingTemplateStep(QueueItem item, int liveSlotId) =>
            owner.RebindPendingBuildingTemplateStep(item, liveSlotId);
        public void RequestQueueUiRefresh() => owner.RequestQueueUiRefresh();
        public ValueTask<bool> RefreshResourceStatusAsync(CancellationToken cancellationToken) =>
            new(owner.RefreshResourceStatusAfterResourceMutationAsync(cancellationToken));
        public async ValueTask<QueueItemSuccessRefreshResult> RefreshConstructionStatusAsync(
            QueueItem item,
            CancellationToken cancellationToken)
        {
            var result = await owner.RefreshConstructionStatusAfterBuildingMutationAsync(
                item,
                cancellationToken);
            return new QueueItemSuccessRefreshResult(result.BuildingsStatusRead, result.StorageStatusRead);
        }
        public ValueTask RefreshCurrentPageStorageStatusAsync(
            BotOptions options,
            string reason,
            CancellationToken cancellationToken) =>
            new(owner.RefreshCurrentPageStorageStatusAsync(options, reason, cancellationToken));
        public ValueTask HandleStorageDependencySucceededAsync(QueueItem item) =>
            new(owner.HandleStorageDependencySucceededAsync(item));
        public ValueTask HandleCropShortageRecoveryStepSucceededAsync(QueueItem item) =>
            new(owner.HandleCropShortageRecoveryStepSucceededAsync(item));
        public async ValueTask RefreshHeroAsync(
            BotOptions options,
            CancellationToken cancellationToken)
        {
            var snapshot = await owner._botService.ReadHeroAttributesAsync(
                options,
                owner.AppendLog,
                cancellationToken);
            await owner.Dispatcher.InvokeAsync(() =>
                owner.ApplyHeroSnapshotToUi(snapshot, "Hero adventure check completed."));
        }
        public ValueTask RefreshTroopTrainingAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.RefreshTroopTrainingUiAfterBuildAsync(item, options, cancellationToken));
        public ValueTask RefreshBreweryAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.RefreshBreweryCelebrationStatusAsync(
                options,
                owner.ResolveBuildingStatusForQueueItem(item),
                cancellationToken));
        public void ScheduleNextReinforcementSend(BotOptions options) =>
            owner.ScheduleNextReinforcementSendAfterSuccess(options);
        public void ApplyProductionBonusResult(string? message) => owner.ApplyProductionBonusResult(message);
        public void ApplyDailyResetResult(string? message) => owner.ApplyDailyResetReadResult(message);
        public void Log(string message) => owner.AppendLog(message);
    }
}
