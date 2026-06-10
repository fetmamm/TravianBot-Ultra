using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private enum QueueExecutionMode
    {
        ContinuousLoop,
        AutoQueue,
    }

    private async Task<bool> ExecuteSingleQueueItemAsync(
        QueueItem item,
        BotOptions options,
        string logPrefix,
        QueueExecutionMode mode,
        CancellationToken cancellationToken)
    {
        var tickSw = Stopwatch.StartNew();
        var terminalCountBefore = await Dispatcher.InvokeAsync(() => _terminalEntries.Count);
        _botService.MarkQueueItemRunning(item.Id);
        // Keep the Dashboard "active village" border on the village this task runs in, so the user can
        // see where the bot is working as it rotates between villages.
        MarkActiveWorkingVillageFromQueueItem(item);
        RefreshQueueUiOnUiThread(item.Id);
        SetActiveAutomationTask(item.TaskName);
        SetActiveFunctionExecution(string.IsNullOrWhiteSpace(item.DisplayName) ? item.TaskName : item.DisplayName);

        // Tracks whether HandleQueueItemSucceededAsync ran a fresh dorf1+dorf2 read for this
        // building mutation. If so, the finally-block snapshot reload (cheap, but reads stale
        // disk cache) is redundant — the live UI already has the freshest data.
        var freshBuildingsRefreshDone = false;

        try
        {
            await _botService.ExecuteQueueItemAsync(options, item, AppendLog, cancellationToken);
            await HandleQueueItemSucceededAsync(item, options, terminalCountBefore, cancellationToken);
            if (NeedsConstructionStatusRefresh(item.TaskName))
            {
                freshBuildingsRefreshDone = true;
            }

            if (mode == QueueExecutionMode.ContinuousLoop
                && string.Equals(item.TaskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase))
            {
                await LoadBuildingsSnapshotIntoUiAsync(cancellationToken);
            }

            AppendLog(FormatQueueSuccessLog(logPrefix, tickSw, item, mode));
            if (mode == QueueExecutionMode.ContinuousLoop)
            {
                _ = Dispatcher.BeginInvoke(() => LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _botService.MarkQueueItemDeferred(item.Id, TimeSpan.Zero);
            return false;
        }
        catch (Exception ex)
        {
            return await HandleQueueItemFailureAsync(item, ex, logPrefix, tickSw, mode);
        }
        finally
        {
            SetActiveAutomationTask(null);
            SetActiveFunctionExecution(null);
            RefreshQueueUiOnUiThread(item.Id);
            if (mode == QueueExecutionMode.AutoQueue
                && IsBuildingMutationTask(item.TaskName)
                && !freshBuildingsRefreshDone)
            {
                try
                {
                    // Failure path or no fresh refresh: roll the UI back to the last-known
                    // disk snapshot so the buildings tab doesn't show pre-task state forever.
                    await LoadBuildingsSnapshotIntoUiAsync(cancellationToken);
                }
                catch
                {
                    // Ignore snapshot reload errors in finally; the UI keeps the previous state.
                }
            }
        }
    }

    private async Task HandleQueueItemSucceededAsync(
        QueueItem item,
        BotOptions options,
        int terminalCountBefore,
        CancellationToken cancellationToken)
    {
        _botService.MarkQueueItemSucceeded(item.Id);
        if (IsResourceUpgradeTask(item.TaskName))
        {
            var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(item.TaskName, terminalCountBefore);
            if (!fastUpdated)
            {
                await LoadResourcesAfterUpgradeAsync(cancellationToken, resourceOnly: true);
            }
        }

        if (IsBuildingMutationTask(item.TaskName))
        {
            await RefreshConstructionStatusAsync(cancellationToken);
            await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
        }
        else if (IsResourceUpgradeTask(item.TaskName))
        {
            await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
        }
        else if (string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    ApplyHeroSnapshotToUi(snapshot, "Hero adventure check completed.");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Hero stats refresh after run failed: {ex.Message}");
            }
        }
        else if (string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RefreshTroopTrainingUiAfterBuildAsync(options, cancellationToken);
            }
            catch (Exception ex)
            {
                AppendLog($"Troop/resource refresh after run failed: {ex.Message}");
            }
        }
        else if (string.Equals(item.TaskName, "run_brewery_celebration", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RefreshBreweryCelebrationStatusAsync(options, _lastBuildingStatus, cancellationToken);
            }
            catch (Exception ex)
            {
                AppendLog($"Brewery celebration refresh after run failed: {ex.Message}");
            }
        }
    }

    private async Task<bool> HandleQueueItemFailureAsync(
        QueueItem item,
        Exception ex,
        string logPrefix,
        Stopwatch timer,
        QueueExecutionMode mode)
    {
        if (await TryHandleTroopsBlockedExecutionAsync(item, ex, logPrefix))
        {
            return true;
        }

        if (mode == QueueExecutionMode.AutoQueue
            && ex is InvalidOperationException ioe
            && ioe.Message.Contains("different thread owns it", StringComparison.OrdinalIgnoreCase))
        {
            _botService.MarkQueueItemExecutionFailed(item.Id);
            _loopController.RequestQueueStop();
            AppendLog($"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | UI thread access error detected. Auto-queue paused to prevent spam.");
            return false;
        }

        if (TryExtractQueueWaitDelay(ex.Message, out var queueWaitDelay))
        {
            if (IsConstructionQueueTask(item.TaskName))
            {
                await Dispatcher.InvokeAsync(() => ApplyConstructionInlineWait(queueWaitDelay));
            }

            if (IsHeroLowHpCooldown(item, ex))
            {
                await ApplyHeroLowHpCooldownUiAsync(queueWaitDelay);
            }

            // Mirror the brewery defer signal onto the Troops-tab celebration card so
            // its badge tracks the dashboard countdown. The continuous-loop brewery
            // task always defers (queue_wait_seconds is its happy-path return), so the
            // success-side RefreshBreweryCelebrationStatusAsync never fires; without
            // this push the troops badge stayed N/A while the dashboard timer ticked.
            if (string.Equals(item.TaskName, "run_brewery_celebration", StringComparison.OrdinalIgnoreCase))
            {
                ApplyBreweryCelebrationDeferSignal(ex.Message, queueWaitDelay);
            }

            var deferred = _botService.MarkQueueItemDeferred(item.Id, queueWaitDelay);
            if (deferred)
            {
                var constructionSuffix = IsConstructionQueueTask(item.TaskName)
                    ? FormatQueueDeferredConstructionSuffix(mode)
                    : string.Empty;
                var payloadChanged = TryExtractDeferredUpgradePayload(ex.Message, item.Payload, out var updatedPayload);
                if (IsConstructionQueueTask(item.TaskName))
                {
                    // Record WHY this construction item deferred so the resource-driven refresh
                    // (RefreshDeferredConstructionWaitsAsync) doesn't resume a queue-full deferral
                    // the moment resources look sufficient, which caused a brief "Ready" flash
                    // before the worker re-deferred on the still-full build queue.
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason] = ConstructionQueueState.IsQueueOccupancyDeferMessage(ex.Message)
                        ? BotOptionPayloadKeys.UpgradeDeferReasonQueueFull
                        : BotOptionPayloadKeys.UpgradeDeferReasonResources;
                    payloadChanged = true;

                    if (string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
                        var retryAt = DateTimeOffset.UtcNow + queueWaitDelay;
                        AppendLog(
                            $"[construction-queue:verbose] queue-full defer classified " +
                            $"id={item.Id} task='{item.TaskName}' village='{villageName}' mode={mode} " +
                            $"waitSeconds={queueWaitDelay.TotalSeconds:F0} retryAt='{FormatQueueServerTime(retryAt)}' " +
                            $"source='{ex.Message.Replace(Environment.NewLine, " ")}'");
                        AppendLog(
                            $"[construction] BUILD QUEUE FULL village='{villageName}'. " +
                            $"No more Construction will run in this village until the first active construction finishes. " +
                            $"Next retry: {FormatQueueServerTime(retryAt)} (in {queueWaitDelay.TotalSeconds:F0}s).");
                    }
                }

                if (payloadChanged)
                {
                    var payloadPersisted = _botService.UpdateDeferredQueueItem(item.Id, updatedPayload);
                    item.Payload = updatedPayload;
                    if (IsConstructionQueueTask(item.TaskName))
                    {
                        AppendLog(
                            $"[construction-queue:verbose] defer payload persistence " +
                            $"id={item.Id} task='{item.TaskName}' persisted={payloadPersisted} " +
                            $"reason='{updatedPayload.GetValueOrDefault(BotOptionPayloadKeys.UpgradeDeferReason, "-")}'");
                    }
                }

                await RefreshFarmListsUiAfterAutoSendIfNeededAsync(item, ex.Message);
                AppendLog($"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
                return true;
            }
        }

        _botService.MarkQueueItemExecutionFailed(item.Id);
        AppendLog(FormatQueueFailureLog(logPrefix, timer, item, ex, mode));
        RaiseAlarmIfQueueItemPermanentlyFailed(item, ex.Message);
        return true;
    }

    private static string FormatQueueSuccessLog(string logPrefix, Stopwatch timer, QueueItem item, QueueExecutionMode mode)
    {
        return mode == QueueExecutionMode.ContinuousLoop
            ? $"{logPrefix} OK {timer.Elapsed.TotalSeconds:F1}s | queue:{item.TaskName}"
            : $"{logPrefix} OK {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName}";
    }

    private static string FormatQueueFailureLog(string logPrefix, Stopwatch timer, QueueItem item, Exception ex, QueueExecutionMode mode)
    {
        return mode == QueueExecutionMode.ContinuousLoop
            ? $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}"
            : $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | {FormatExceptionForLog(ex)}";
    }

    private static string FormatQueueDeferredConstructionSuffix(QueueExecutionMode mode)
    {
        return mode == QueueExecutionMode.ContinuousLoop
            ? " | construction wait timer updated; continuing with next enabled group; no Hero refresh was triggered by this defer"
            : " | construction wait timer updated; continuing with other ready tasks; no Hero refresh was triggered by this defer";
    }
}
