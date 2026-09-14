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

        public void MarkRunning(QueueItem item)
        {
            owner.MarkDueConstructionForPreSleepFill(item);
            owner.RefreshConstructFasterPayloadForExecution(item);
            owner._botService.MarkQueueItemRunning(item.Id);
            owner.RefreshQueueUiOnUiThread(item.Id);
            owner.SetActiveAutomationTask(item.TaskName);
            owner.SetActiveFunctionExecution(
                string.IsNullOrWhiteSpace(item.DisplayName) ? item.TaskName : item.DisplayName);
        }

        public async ValueTask<QueueItemGuardResult> RunPreExecutionGuardsAsync(
            QueueItem item,
            BotOptions options,
            string logPrefix,
            Stopwatch timer,
            CancellationToken cancellationToken)
        {
            if (owner.TryHandleUpgradeWaitingForConstruct(item, logPrefix, timer))
            {
                return new QueueItemGuardResult(true, false);
            }

            var constructRefresh =
                await owner.TryRefreshConstructTargetVillageStatusBeforeGuardAsync(
                    item,
                    options,
                    cancellationToken);
            if (constructRefresh.FreshStatus is not null
                && owner.TryHandleExistingConstructBeforeGuards(
                    item,
                    constructRefresh.FreshStatus,
                    logPrefix,
                    timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            if (constructRefresh.FreshStatus is not null
                && owner.TryHandleOccupiedConstructSlotBeforeGuards(
                    item,
                    constructRefresh.FreshStatus,
                    logPrefix,
                    timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            if (constructRefresh.CanUseCache
                && await owner.TryHandleConstructQueueFullBeforeRequirementGuardAsync(
                    item,
                    logPrefix,
                    timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            if (constructRefresh.CanUseCache
                && await owner.TryHandleConstructRequirementPreRunGuardAsync(
                    item,
                    logPrefix,
                    timer))
            {
                return new QueueItemGuardResult(true, true);
            }

            return QueueItemGuardResult.NotHandled;
        }

        public BotOptions ApplyQueueItemOptions(BotOptions options, QueueItem item) =>
            owner.ApplyHeroResourceSettingsForQueueItem(options, item);

        public CancellationToken BeginQueueItemOperation(
            QueueItem item,
            CancellationToken cancellationToken) =>
            IsDemolition(item)
                ? owner.BeginDemolishOperation(item, cancellationToken)
                : cancellationToken;

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
            new(owner.TryRecoverMissingBuildingUpgradeAsync(
                item,
                options,
                executionResult,
                logPrefix,
                timer,
                cancellationToken));

        public ValueTask<bool> HandleSucceededAsync(
            QueueItem item,
            BotOptions options,
            BotTaskExecutionResult executionResult,
            CancellationToken cancellationToken) =>
            new(owner.HandleQueueItemSucceededAsync(item, options, executionResult, cancellationToken));

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
            new(owner.HandleQueueItemFailureAsync(
                item,
                exception,
                logPrefix,
                timer,
                ToQueueExecutionMode(mode)));

        public async ValueTask FinalizeExecutionAsync(
            QueueItem item,
            AutomationRunMode mode,
            bool freshBuildingsRefreshDone,
            CancellationToken cancellationToken)
        {
            if (IsDemolition(item))
            {
                owner.CompleteDemolishOperation(item.Id);
            }
            owner.SetActiveAutomationTask(null);
            owner.SetActiveFunctionExecution(null);
            owner.RefreshQueueUiOnUiThread(item.Id);
            if (!cancellationToken.IsCancellationRequested
                && mode == AutomationRunMode.AutoQueue
                && IsBuildingMutationTask(item.TaskName)
                && !freshBuildingsRefreshDone)
            {
                try
                {
                    await owner.LoadBuildingsSnapshotIntoUiAsync(cancellationToken);
                }
                catch
                {
                    // The UI keeps its previous state when the last-known snapshot cannot be restored.
                }
            }
        }

        public void Log(string message) => owner.AppendLog(message);

        private static QueueExecutionMode ToQueueExecutionMode(AutomationRunMode mode) =>
            mode == AutomationRunMode.ContinuousLoop
                ? QueueExecutionMode.ContinuousLoop
                : QueueExecutionMode.AutoQueue;

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
