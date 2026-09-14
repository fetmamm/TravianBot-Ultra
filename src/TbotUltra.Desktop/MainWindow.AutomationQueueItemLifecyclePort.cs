using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowAutomationQueueItemLifecyclePort(MainWindow owner)
        : IAutomationQueueItemLifecyclePort
    {
        private readonly AutomationMissingBuildingUpgradeRecovery _missingBuildingUpgradeRecovery =
            new(new MainWindowAutomationMissingBuildingUpgradeRecoveryPort(owner));
        private readonly AutomationConstructLiveReconciliation _constructLiveReconciliation =
            new(new MainWindowAutomationConstructLiveReconciliationPort(owner));
        private readonly AutomationConstructPreflight _constructPreflight =
            new(new MainWindowAutomationConstructPreflightPort(owner));
        private readonly AutomationConstructionRequirementGuard _constructionRequirementGuard =
            new(new MainWindowAutomationConstructionRequirementGuardPort(owner));
        private readonly AutomationQueueItemSuccess _queueItemSuccess =
            new(new MainWindowAutomationQueueItemSuccessPort(owner));
        private readonly AutomationQueueItemFailure _queueItemFailure =
            new(new MainWindowAutomationQueueItemFailurePort(owner));

        public bool IsAllowedByAutomationSettings(QueueItem item) =>
            owner.IsQueueItemAllowedByAutomationSettings(item);

        public IDisposable BeginExecutionScope(QueueItem item)
        {
            var account = owner._accountStore.ActiveAccountName();
            var logContext = AutomationLogContext.BeginScope(
                account: account,
                task: item.TaskName,
                village: GetQueueItemVillageName(item),
                villageKey: owner.GetQueueItemVillageKey(item));
            var trainingSettings = string.Equals(
                item.TaskName,
                "build_troops",
                StringComparison.OrdinalIgnoreCase)
                ? TroopTrainingExecutionSettings.BeginScope(
                    () => TroopTrainingSettingsStore.Load(
                        owner._projectRoot,
                        account,
                        owner.GetQueueItemVillageKey(item)),
                    owner.AppendLog)
                : null;
            return new QueueItemExecutionScope(logContext, trainingSettings);
        }

        public BotOptions LoadCurrentOptions() => owner.LoadBotOptions();

        public void MarkDueConstructionForPreSleepFill(QueueItem item) =>
            owner.MarkDueConstructionForPreSleepFill(item);

        public void RefreshConstructFasterPayloadForExecution(QueueItem item) =>
            owner.RefreshConstructFasterPayloadForExecution(item);

        public bool MarkRunning(Guid itemId) => owner._botService.MarkQueueItemRunning(itemId);

        public void RefreshQueueUi(Guid itemId) => owner.RefreshQueueUiOnUiThread(itemId);

        public void SetActiveAutomationTask(string? taskName) => owner.SetActiveAutomationTask(taskName);

        public void SetActiveFunctionExecution(string? displayName) =>
            owner.SetActiveFunctionExecution(displayName);

        public async ValueTask<QueueItemGuardResult> RunPreExecutionGuardsAsync(
            QueueItem item,
            BotOptions options,
            string logPrefix,
            Stopwatch timer,
            CancellationToken cancellationToken)
        {
            if (_constructionRequirementGuard.TryHandleUpgradeWaitingForConstruct(item, logPrefix, timer))
            {
                return new QueueItemGuardResult(true, false);
            }

            var constructRefresh = await _constructPreflight.RefreshTargetStatusAsync(
                item,
                options,
                cancellationToken);
            if (constructRefresh.FreshStatus is not null
                && _constructLiveReconciliation.TryHandleExistingConstruct(
                    item,
                    constructRefresh.FreshStatus,
                    logPrefix,
                    timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            if (constructRefresh.FreshStatus is not null
                && _constructLiveReconciliation.TryHandleOccupiedSlot(
                    item,
                    constructRefresh.FreshStatus,
                    logPrefix,
                    timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            if (constructRefresh.CanUseCache
                && await _constructPreflight.TryHandleQueueFullAsync(item, logPrefix, timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            if (constructRefresh.CanUseCache
                && await _constructionRequirementGuard.TryHandleAsync(item, logPrefix, timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            return QueueItemGuardResult.NotHandled;
        }

        public BotOptions ApplyQueueItemOptions(BotOptions options, QueueItem item) =>
            owner.ApplyHeroResourceSettingsForQueueItem(options, item);

        public CancellationToken BeginDemolitionOperation(
            QueueItem item,
            CancellationToken cancellationToken) =>
            owner.BeginDemolishOperation(item, cancellationToken);

        public ValueTask<BotTaskExecutionResult> ExecuteWorkerAsync(
            BotOptions options,
            QueueItem item,
            CancellationToken cancellationToken) =>
            new(owner._botService.ExecuteQueueItemAsync(
                options,
                item,
                owner.AppendLog,
                cancellationToken));

        public ValueTask<bool> TryRecoverMissingBuildingUpgradeAsync(
            QueueItem item,
            BotOptions options,
            BotTaskExecutionResult executionResult,
            string logPrefix,
            Stopwatch timer,
            CancellationToken cancellationToken) =>
            _missingBuildingUpgradeRecovery.TryRecoverAsync(
                item,
                options,
                executionResult,
                logPrefix,
                timer,
                cancellationToken);

        public ValueTask<bool> HandleSucceededAsync(
            QueueItem item,
            BotOptions options,
            BotTaskExecutionResult executionResult,
            CancellationToken cancellationToken) =>
            _queueItemSuccess.HandleAsync(item, options, executionResult, cancellationToken);

        public bool IsLoadBuildingsSnapshot(QueueItem item) =>
            string.Equals(
                item.TaskName,
                "load_buildings_snapshot",
                StringComparison.OrdinalIgnoreCase);

        public ValueTask LoadBuildingsSnapshotAsync(CancellationToken cancellationToken) =>
            new(owner.LoadBuildingsSnapshotIntoUiAsync(
                cancellationToken,
                reconcileQueueWithFreshSnapshot: true));

        public void MarkNetworkConnectionHealthy() => owner.MarkNetworkConnectionHealthy();

        public void PublishLastScan() =>
            _ = owner.Dispatcher.BeginInvoke(
                () => owner.LastScanInfoTextBlock.Text = $"Last scan: {owner.GetServerNow():HH:mm:ss}");

        public bool IsDemolition(QueueItem item) => IsDemolishQueueItem(item);

        public bool WasDemolitionStopped(Guid itemId) => owner.WasDemolishOperationStopped(itemId);

        public bool MarkDeferred(Guid itemId, TimeSpan delay) =>
            owner._botService.MarkQueueItemDeferred(itemId, delay);

        public TimeSpan NextNetworkRetryDelay() => owner._automationNetworkBackoff.NextRetryDelay();

        public void MarkNetworkUnavailable(TimeSpan retryDelay) =>
            owner._automationNetworkBackoff.MarkUnavailable(retryDelay);

        public ValueTask HoldAccountAutomationAsync(AccountAccessException exception) =>
            new(owner.HoldAccountAutomationAsync(exception));

        public ValueTask HandleUnexpectedTravianLanguageAsync(
            UnexpectedTravianLanguageException exception) =>
            new(owner.HandleUnexpectedTravianLanguageAsync(exception));

        public ValueTask<bool> HandleTaskSpecificFailureAsync(
            QueueItem item,
            Exception exception,
            string logPrefix,
            Stopwatch timer,
            AutomationRunMode mode) =>
            _queueItemFailure.HandleAsync(
                item,
                exception,
                logPrefix,
                timer,
                mode);

        public void CompleteDemolitionOperation(Guid itemId) => owner.CompleteDemolishOperation(itemId);

        public ValueTask RestoreBuildingsSnapshotAsync(CancellationToken cancellationToken) =>
            new(owner.LoadBuildingsSnapshotIntoUiAsync(cancellationToken));

        public void Log(string message) => owner.AppendLog(message);

        private sealed class QueueItemExecutionScope(
            IDisposable logContext,
            IDisposable? trainingSettings) : IDisposable
        {
            public void Dispose()
            {
                trainingSettings?.Dispose();
                logContext.Dispose();
            }
        }
    }
}
