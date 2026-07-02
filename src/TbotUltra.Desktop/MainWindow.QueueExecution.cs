using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private enum QueueExecutionMode
    {
        ContinuousLoop,
        AutoQueue,
    }

    // How many consecutive requirement defers a construction item may accumulate before it is abandoned
    // (marked Failed). At the worker's ~5 min requirement-defer cadence this is roughly an hour of retries,
    // long enough for a genuinely in-progress prerequisite to finish but bounded so a never-coming one
    // doesn't defer forever.
    private const int MaxConsecutiveRequirementDefers = 12;

    // Whether the queue item's village currently has a browser-confirmed construction in progress. Used to
    // hold off abandoning a requirement-stalled item while the prerequisite might be that active build.
    private bool VillageHasActiveConstruction(QueueItem item)
    {
        var name = NormalizeVillageName(GetQueueItemVillageName(item));
        var status = name is not null && _villageStatusCacheByName.TryGetValue(name, out var cached)
            ? cached
            : _lastBuildingStatus;
        return status is not null
            && ConstructionQueueState.ResolveCurrentActiveConstructions(status).Count > 0;
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
            var effectiveOptions = ApplyHeroResourceSettingsForQueueItem(options, item);
            var executionResult = await _botService.ExecuteQueueItemAsync(effectiveOptions, item, AppendLog, cancellationToken);
            freshBuildingsRefreshDone = await HandleQueueItemSucceededAsync(
                item,
                options,
                executionResult,
                terminalCountBefore,
                cancellationToken);

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

    private async Task<bool> HandleQueueItemSucceededAsync(
        QueueItem item,
        BotOptions options,
        BotTaskExecutionResult executionResult,
        int terminalCountBefore,
        CancellationToken cancellationToken)
    {
        _botService.MarkQueueItemSucceeded(item.Id);

        // Confirmed already-built construct: the worker found the target slot already holds the building, so
        // the task can never construct. Remove it from the queue (not leave it as junk) — the user wants a
        // construct whose building already exists cleared out, and the worker only returns this after a live
        // confirmation. Nothing else to refresh: the slot already has the building.
        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && executionResult.LastTask?.ConstructionOutcome == ConstructionTaskOutcome.AlreadyExists)
        {
            if (_botService.RemoveQueueItem(item.Id))
            {
                AppendLog($"[queue] removed construct task — building already exists (confirmed). {executionResult.LastTask?.Message}");
            }

            RequestQueueUiRefresh();
            return false;
        }

        var fullConstructionRefreshDone = false;
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
            var refreshResult = await RefreshConstructionStatusAfterBuildingMutationAsync(options, executionResult, cancellationToken);
            fullConstructionRefreshDone = refreshResult.FullStatusRead;
            if (!refreshResult.StorageStatusRead)
            {
                await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
            }
            await HandleStorageDependencySucceededAsync(item);
        }
        else if (IsResourceUpgradeTask(item.TaskName))
        {
            await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
        }
        else if (string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(item.TaskName, "spend_hero_attribute_points", StringComparison.OrdinalIgnoreCase))
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
                await RefreshTroopTrainingUiAfterBuildAsync(item, options, cancellationToken);
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
        else if (string.Equals(item.TaskName, "send_reinforcements_between_villages", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleNextReinforcementSendAfterSuccess(options);
        }

        return fullConstructionRefreshDone;
    }

    private async Task<(bool FullStatusRead, bool StorageStatusRead)> RefreshConstructionStatusAfterBuildingMutationAsync(
        BotOptions options,
        BotTaskExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var outcome = executionResult.LastTask?.ConstructionOutcome ?? ConstructionTaskOutcome.UnknownSuccess;
        if (outcome == ConstructionTaskOutcome.QueuedOrInProgress)
        {
            try
            {
                await RefreshCurrentPageStorageStatusAsync(options, "construction_success_quick", cancellationToken);
                AppendLog("[construction-refresh] current-page refresh used for queued/in-progress construction; skipped full dorf1+dorf2 read.");
                return (FullStatusRead: false, StorageStatusRead: true);
            }
            catch (Exception ex)
            {
                AppendLog($"[construction-refresh] current-page refresh failed ({ex.Message}); falling back to full construction status.");
            }
        }

        await RefreshConstructionStatusAsync(cancellationToken);
        return (FullStatusRead: true, StorageStatusRead: false);
    }

    private async Task<bool> HandleQueueItemFailureAsync(
        QueueItem item,
        Exception ex,
        string logPrefix,
        Stopwatch timer,
        QueueExecutionMode mode)
    {
        if (BrowserFailureClassifier.IsTargetCrash(ex))
        {
            var retryDelay = TimeSpan.FromSeconds(15);
            if (_botService.MarkQueueItemDeferred(item.Id, retryDelay))
            {
                AppendLog(
                    $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                    $"browser target crashed; fresh session retry in {retryDelay.TotalSeconds:F0}s");
                return true;
            }
        }

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

            if (string.Equals(item.TaskName, "run_town_hall_celebration", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTownHallCelebrationDeferSignal(item, ex.Message, queueWaitDelay);
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
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason] =
                        ConstructionQueueState.IsQueueOccupancyDeferMessage(ex.Message)
                            ? BotOptionPayloadKeys.UpgradeDeferReasonQueueFull
                            : ConstructionQueueState.IsConstructionInProgressDeferMessage(ex.Message)
                                ? BotOptionPayloadKeys.UpgradeDeferReasonInProgress
                                : ConstructionQueueState.IsConstructionStorageCapacityDeferMessage(ex.Message)
                                    ? BotOptionPayloadKeys.UpgradeDeferReasonStorageCapacity
                                    : ConstructionQueueState.IsConstructionRequirementDeferMessage(ex.Message)
                                        ? BotOptionPayloadKeys.UpgradeDeferReasonRequirements
                                        : ConstructionQueueState.IsConstructionResourceDeferMessage(ex.Message)
                                            ? BotOptionPayloadKeys.UpgradeDeferReasonResources
                                            : BotOptionPayloadKeys.UpgradeDeferReasonRetry;
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                        ConstructionQueueState.CurrentDeferClassificationVersion;
                    payloadChanged = true;

                    // Safety net for an unsatisfiable requirement. Requirement defers don't consume Retries
                    // (the prerequisite could still arrive), so without a bound a construct whose prerequisite
                    // never comes — e.g. the desktop cascade missed a cross-village/not-yet-loaded dependent —
                    // would defer forever. Count consecutive requirement defers and abandon (mark Failed +
                    // alarm) once the prerequisite has clearly not been built after many retries. Any other
                    // defer reason resets the counter below.
                    if (string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var requirementDeferCount =
                            (TryGetIntPayloadValue(item.Payload, BotOptionPayloadKeys.RequirementDeferCount) ?? 0) + 1;
                        updatedPayload[BotOptionPayloadKeys.RequirementDeferCount] = requirementDeferCount.ToString();

                        // Never abandon while the village is actively building something — the prerequisite
                        // may be that in-progress construction (e.g. a user-started Academy 15 that Hospital
                        // waits on). Only give up once the village build queue is idle and the requirement is
                        // still unmet, which means the prerequisite is genuinely not coming.
                        if (requirementDeferCount >= MaxConsecutiveRequirementDefers
                            && !VillageHasActiveConstruction(item))
                        {
                            _botService.MarkQueueItemExecutionFailed(item.Id);
                            AppendLog(
                                $"{logPrefix} ABANDONED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                                $"requirement still unmet after {requirementDeferCount} retries — the prerequisite " +
                                $"building is not built, queued or in progress. Removed from the active queue. " +
                                $"Source='{ex.Message.Replace(Environment.NewLine, " ")}'");
                            RaiseAlarmIfQueueItemPermanentlyFailed(item, ex.Message);
                            await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
                            return true;
                        }
                    }
                    else
                    {
                        // Progress is possible again — start a fresh count next time requirements stall.
                        updatedPayload.Remove(BotOptionPayloadKeys.RequirementDeferCount);
                    }

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
                    else if (string.Equals(
                        updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                        BotOptionPayloadKeys.UpgradeDeferReasonInProgress,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? "-";
                        var retryAt = DateTimeOffset.UtcNow + queueWaitDelay;
                        AppendLog(
                            $"[construction-queue:verbose] in-progress defer classified " +
                            $"id={item.Id} task='{item.TaskName}' village='{villageName}' mode={mode} " +
                            $"retryAt='{FormatQueueServerTime(retryAt)}'; later construction remains eligible.");
                    }
                }

                if (payloadChanged)
                {
                    var payloadPersisted = _botService.UpdateDeferredQueueItem(item.Id, updatedPayload);
                    item.Payload = updatedPayload;
                    if (IsConstructionQueueTask(item.TaskName) && !payloadPersisted)
                    {
                        AppendLog(
                            $"[construction-queue] construction payload persistence failed " +
                            $"id={item.Id} task='{item.TaskName}' " +
                            $"reason='{updatedPayload.GetValueOrDefault(BotOptionPayloadKeys.UpgradeDeferReason, "-")}'");
                    }
                }

                if (IsConstructionQueueTask(item.TaskName))
                {
                    await TryHandleStorageCapacityDependencyAsync(item, updatedPayload);
                }

                await RefreshFarmListsUiAfterAutoSendIfNeededAsync(item, ex.Message);
                AppendLog($"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
                // A building-mutation task can start one build (e.g. via hero resource transfer) and then defer
                // because the NEXT level lacks resources. That deferral skips the success-path construction
                // refresh, so the cached ActiveBuildCount stays stale (0) and the overview would paint EVERY
                // idle build slot amber instead of the just-started one green. Re-read the current village's
                // construction status (the browser is already on it) so the freshly started build is cached
                // before the icons repaint below.
                if (IsBuildingMutationTask(item.TaskName))
                {
                    try
                    {
                        await RefreshConstructionStatusAsync(_loopController.AcquireSessionScopeToken());
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Construction status refresh after defer skipped: {refreshEx.Message}");
                    }
                }

                // build_troops always DEFERS on its happy path: it queues troops, then returns
                // queue_wait_seconds for the cooldown. That skips the success-path troop refresh, so the
                // per-village troop-training queue cache (and the Troops B/S/W icon) stayed grey even though
                // a training queue is now active. Re-read the village's queues when troops were actually
                // queued, so the icon turns green and the state is cached (and thus persisted across restart).
                if (string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("queued", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await RefreshTroopTrainingUiAfterBuildAsync(item, LoadBotOptions(), _loopController.AcquireSessionScopeToken());
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Troop training refresh after deferred build skipped: {refreshEx.Message}");
                    }
                }

                // hero_manage deferred for the full revive time. Tag the item so the periodic 16s refresh
                // can release it early if the user revives the hero sooner (e.g. with a bucket) — otherwise
                // adventures would idle until the original revive countdown elapsed.
                if (string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("hero_reviving", StringComparison.OrdinalIgnoreCase))
                {
                    var revivingPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
                    {
                        [HeroDeferReasonKey] = HeroDeferReasonReviving,
                    };
                    if (_botService.UpdateDeferredQueueItem(item.Id, revivingPayload))
                    {
                        item.Payload = revivingPayload;
                    }
                }

                // Repaint the per-village overview icons so the deferred task shows its amber "waiting" state.
                await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
                return true;
            }
        }

        _botService.MarkQueueItemExecutionFailed(item.Id);
        HandleStorageDependencyFailed(item, ex.Message);
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
