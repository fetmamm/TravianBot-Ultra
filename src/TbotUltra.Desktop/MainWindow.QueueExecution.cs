using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
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
        var status = ResolveBuildingStatusForQueueItem(item);
        return status is not null
            && ConstructionQueueState.ResolveCurrentActiveConstructions(status).Count > 0;
    }

    private ConstructionRequirementGuardResult ResolveConstructRequirementGuardForQueueItem(
        QueueItem item,
        DateTimeOffset now)
    {
        var context = ResolveConstructRequirementContextForQueueItem(item);
        if (context.Status is null)
        {
            return ConstructionRequirementGuardResult.None;
        }

        return ConstructionDependencyGate.ResolveConstructRequirementGuard(
            item,
            context.Status,
            context.SameVillageItems,
            now);
    }

    private (VillageStatus? Status, IReadOnlyList<QueueItem> SameVillageItems) ResolveConstructRequirementContextForQueueItem(
        QueueItem item)
    {
        var status = ResolveBuildingStatusForQueueItem(item);
        var villageKey = GetQueueItemVillageKey(item);
        var sameVillageFilter = BuildSameVillageQueueFilter(item);
        var sameVillageItems = GetActiveQueueItems()
            .Where(other => other.Id != item.Id)
            .Where(other =>
            {
                if (villageKey is null)
                {
                    return sameVillageFilter(other);
                }

                var otherKey = GetQueueItemVillageKey(other);
                return otherKey is null
                    || string.Equals(otherKey, villageKey, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        return (status, sameVillageItems);
    }

    private bool ConstructHasQueuedOrActivePrerequisite(QueueItem item, DateTimeOffset now)
    {
        var result = ResolveConstructRequirementGuardForQueueItem(item, now);
        if (result.Action is ConstructionRequirementGuardAction.DeferForActivePrerequisite
            or ConstructionRequirementGuardAction.DeferForQueuedPrerequisite)
        {
            return true;
        }

        return result.Action == ConstructionRequirementGuardAction.None
            ? VillageHasActiveConstruction(item)
            : false;
    }

    private async Task<bool> HandleQueueItemSucceededAsync(
        QueueItem item,
        BotOptions options,
        BotTaskExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        _botService.MarkQueueItemSucceeded(item.Id);

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && TryExtractPayloadInt(
                executionResult.LastTask?.Message,
                BotOptionPayloadKeys.BuildingConstructSlotId,
                out var effectiveConstructSlot))
        {
            RebindPendingBuildingUpgrades(item, effectiveConstructSlot);
            RebindPendingBuildingTemplateStep(item, effectiveConstructSlot);
        }

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
        var resourceStatusRead = false;
        if (IsResourceUpgradeTask(item.TaskName))
        {
            // dorf1 mirror of the building-mutation refresh: always re-read the just-worked village's
            // resource fields and cache them (village-specific), so field levels never go stale. The old
            // "fast update" patched the SELECTED village's rows from log lines — wrong village in a
            // multi-village account — and never touched the cache.
            resourceStatusRead = await RefreshResourceStatusAfterResourceMutationAsync(cancellationToken);
        }

        if (IsBuildingMutationTask(item.TaskName))
        {
            var refreshResult = await RefreshConstructionStatusAfterBuildingMutationAsync(item, cancellationToken);
            fullConstructionRefreshDone = refreshResult.BuildingsStatusRead;
            if (!refreshResult.StorageStatusRead)
            {
                await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
            }
            await HandleStorageDependencySucceededAsync(item);
        }
        else if (IsResourceUpgradeTask(item.TaskName))
        {
            if (!resourceStatusRead)
            {
                await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
            }
            if (item.Payload.ContainsKey(BotOptionPayloadKeys.CropShortageRecoveryParentId))
            {
                await HandleCropShortageRecoveryStepSucceededAsync(item);
            }
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
                await RefreshBreweryCelebrationStatusAsync(
                    options,
                    ResolveBuildingStatusForQueueItem(item),
                    cancellationToken);
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
        else if (string.Equals(item.TaskName, "activate_production_bonus", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProductionBonusResult(executionResult.LastTask?.Message);
        }
        else if (string.Equals(item.TaskName, "read_daily_reset", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(item.TaskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase))
        {
            // read_daily_reset carries the reset hour; collect_daily_quests piggybacks it from the open dialog.
            ApplyDailyResetReadResult(executionResult.LastTask?.Message);
        }

        return fullConstructionRefreshDone;
    }

    private async Task<(bool BuildingsStatusRead, bool StorageStatusRead)> RefreshConstructionStatusAfterBuildingMutationAsync(
        QueueItem item,
        CancellationToken cancellationToken)
    {
        // A confirmed construct/upgrade redirects to Dorf2. Reuse that already-loaded overview so the
        // next level can open directly from it instead of forcing Dorf2 -> Dorf1 -> Dorf2. The quick read
        // is accepted only when all 22 slots, an authoritative construction queue and the exact target
        // village coordinates are present. Any uncertainty retains the old full refresh as the fallback.
        try
        {
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            var currentStatus = await _botService.ReadCurrentBuildingOverviewStatusAsync(
                options,
                AppendLog,
                cancellationToken);
            var expectedVillageKey = GetQueueItemVillageKey(item);
            if (!ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot(currentStatus, expectedVillageKey))
            {
                throw new InvalidOperationException(
                    $"Current Dorf2 snapshot was incomplete or belonged to another village " +
                    $"(expected={expectedVillageKey ?? "-"}, " +
                    $"observed={ResolveStatusVillageKey(currentStatus) ?? "-"}, " +
                    $"slots={currentStatus.Buildings.Count}, " +
                    $"queueAuthoritative={currentStatus.ActiveConstructionsFromOverview}).");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var existing = ResolveBuildingStatusForQueueItem(item);
                var merged = existing is null
                    ? currentStatus
                    : ConstructionMutationRefreshPolicy.MergeCurrentDorf2Snapshot(existing, currentStatus);
                SetActiveWorkingVillageFromStatus(merged);
                CacheVillageStatus(merged);
                ReconcilePendingBuildingQueueWithLiveStatus(merged);
                if (!IsStatusForSelectedVillage(merged))
                {
                    return;
                }

                _lastBuildingStatus = merged;
                ApplyVillageStatusToUi(merged);
                PopulateBuildingsTab(merged);
            });

            AppendLog(
                $"[construction-refresh] reused authoritative current Dorf2 after '{item.TaskName}'; " +
                "skipped Dorf1 navigation.");
            return (BuildingsStatusRead: true, StorageStatusRead: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[construction-refresh] current Dorf2 refresh failed ({ex.Message}); " +
                "falling back to full Dorf1+Dorf2 status.");
            await RefreshConstructionStatusAsync(cancellationToken);
            return (BuildingsStatusRead: true, StorageStatusRead: false);
        }
    }

    // dorf1 counterpart of RefreshConstructionStatusAfterBuildingMutationAsync: re-reads the just-worked
    // village's resource fields (dorf1) and caches them keyed by that village's coordinates, then repaints
    // the resource UI only when it is the selected village. The browser is already on the worked village
    // (forceCurrentVillage), so this stays village-specific — a resource upgrade in village B never writes
    // village A's rows. Returns true when the read succeeded.
    private async Task<bool> RefreshResourceStatusAfterResourceMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            var status = await ReadVillageStatusWithRetryAsync(
                options,
                cancellationToken,
                resourceOnly: true,
                forceCurrentVillage: true);
            await Dispatcher.InvokeAsync(() =>
            {
                SetActiveWorkingVillageFromStatus(status);
                CacheVillageStatus(status);
                if (IsStatusForSelectedVillage(status))
                {
                    ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
                }
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] full dorf1 read after resource task failed: {ex.Message}");
            return false;
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

        if (TryHandleTownHallUnavailableExecution(item, ex, logPrefix))
        {
            return true;
        }

        // Prefer the typed defer signal (TaskWaitException.DelaySeconds) over parsing the message;
        // message parsing remains as a fallback for exceptions that carry the wait hint only as text.
        TimeSpan queueWaitDelay;
        bool hasQueueWait;
        if (ex is TaskWaitException typedWait)
        {
            queueWaitDelay = TimeSpan.FromSeconds(typedWait.DelaySeconds);
            hasQueueWait = true;
        }
        else
        {
            hasQueueWait = TryExtractQueueWaitDelay(ex.Message, out queueWaitDelay);
        }

        if (hasQueueWait)
        {
            if (IsConstructionQueueTask(item.TaskName)
                && ConstructionQueueState.IsConstructionRequirementDeferMessage(ex.Message)
                && TryResolveConstructActivePrerequisiteDelay(
                    item,
                    DateTimeOffset.UtcNow,
                    out var dependencyDelay))
            {
                queueWaitDelay = dependencyDelay.Delay;
                AppendLog(
                    $"[construction-dependency:verbose] worker requirement wait aligned to active prerequisite " +
                    $"id={item.Id} task='{item.TaskName}' waitSeconds={queueWaitDelay.TotalSeconds:F0} " +
                    $"requirements='{dependencyDelay.Detail}'");
            }

            var isHumanizeDefer = IsConstructionQueueTask(item.TaskName)
                && ex.Message.Contains("humanized construction start delay", StringComparison.OrdinalIgnoreCase);
            if (IsConstructionQueueTask(item.TaskName))
            {
                var humanizeVillage = isHumanizeDefer ? GetQueueItemVillageKey(item) : null;
                TimeSpan? humanizeWait = isHumanizeDefer ? queueWaitDelay : null;
                await Dispatcher.InvokeAsync(() => ApplyConstructionInlineWait(queueWaitDelay, humanizeVillage, humanizeWait));
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

            if (IsConstructionQueueTask(item.TaskName)
                && ConstructionQueueState.IsQueueOccupancyDeferMessage(ex.Message)
                && TryExtractPayloadInt(
                    ex.Message,
                    BotOptionPayloadKeys.QueueHumanizeExtraSeconds,
                    out var queueHumanizeExtraSeconds))
            {
                var observedAt = DateTimeOffset.UtcNow;
                var effectiveReadyAt = observedAt + queueWaitDelay;
                var rawSlotFinishAt = effectiveReadyAt.AddSeconds(-queueHumanizeExtraSeconds);
                var trigger = item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill)
                    ? "pre-sleep"
                    : item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionLoginFill)
                        ? "login"
                        : "normal";
                AppendLog(
                    $"[construction-timing] village='{GetQueueItemVillageName(item) ?? "-"}' " +
                    $"task='{item.TaskName}' trigger={trigger} observedAt='{observedAt:O}' " +
                    $"rawSlotFinishAt='{rawSlotFinishAt:O}' humanDelaySeconds={queueHumanizeExtraSeconds} " +
                    $"effectiveReadyAt='{effectiveReadyAt:O}' navigation=completed.");
            }

            var deferred = _botService.MarkQueueItemDeferred(item.Id, queueWaitDelay);
            if (deferred)
            {
                var constructionSuffix = IsConstructionQueueTask(item.TaskName)
                    ? FormatQueueDeferredConstructionSuffix(mode)
                    : string.Empty;
                var payloadChanged = TryExtractDeferredUpgradePayload(ex.Message, item.Payload, out var updatedPayload);
                if (IsDemolishQueueItem(item)
                    && TryExtractPayloadInt(ex.Message, "demolish_server_wait_seconds", out var serverWaitSeconds)
                    && TryExtractPayloadInt(ex.Message, BotOptionPayloadKeys.DemolishDelaySeconds, out var demolishDelaySeconds))
                {
                    updatedPayload[BotOptionPayloadKeys.DemolishServerFinishAtUnixSeconds] =
                        DateTimeOffset.UtcNow.AddSeconds(serverWaitSeconds).ToUnixTimeSeconds().ToString();
                    updatedPayload[BotOptionPayloadKeys.DemolishDelaySeconds] = demolishDelaySeconds.ToString();
                    payloadChanged = true;
                }
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
                                : ConstructionQueueState.IsCropShortageDeferMessage(ex.Message)
                                    ? BotOptionPayloadKeys.UpgradeDeferReasonCropShortage
                                : ConstructionQueueState.IsConstructionRequirementDeferMessage(ex.Message)
                                        ? BotOptionPayloadKeys.UpgradeDeferReasonRequirements
                                        : ConstructionQueueState.IsConstructionResourceDeferMessage(ex.Message)
                                            ? BotOptionPayloadKeys.UpgradeDeferReasonResources
                                            : ConstructionQueueState.IsConstructionHumanizeDeferMessage(ex.Message)
                                                ? BotOptionPayloadKeys.UpgradeDeferReasonHumanize
                                                : BotOptionPayloadKeys.UpgradeDeferReasonRetry;
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                        ConstructionQueueState.CurrentDeferClassificationVersion;
                    payloadChanged = true;

                    // The pre-sleep fill flag is valid for exactly one execution attempt — this attempt
                    // just ran, so drop it. The sweep re-flags the item if it defers into the window again.
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied);
                    if (!TryExtractPayloadInt(
                            ex.Message,
                            BotOptionPayloadKeys.QueueHumanizeExtraSeconds,
                            out _))
                    {
                        updatedPayload.Remove(BotOptionPayloadKeys.QueueHumanizeExtraSeconds);
                    }
                    // The immediate-fill override stays meaningful only while a construction was started
                    // or its own Travian category is full. Resource/requirement/storage/retry waits are
                    // a real unstarted head and end the burst for later rows too.
                    var fillCanContinue = string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonInProgress,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            updatedPayload[BotOptionPayloadKeys.UpgradeDeferReason],
                            BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
                            StringComparison.OrdinalIgnoreCase);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);
                    if (!fillCanContinue)
                    {
                        ClearConstructionLoginFillForBlockedHead(item, "empty-queue");
                    }

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
                            && !ConstructHasQueuedOrActivePrerequisite(item, DateTimeOffset.UtcNow))
                        {
                            var payloadPersisted = PatchDeferredQueuePayload(item, updatedPayload);
                            item.Payload = updatedPayload;
                            if (!payloadPersisted)
                            {
                                AppendLog(
                                    $"[construction-queue] requirement-abandon payload persistence failed " +
                                    $"id={item.Id} task='{item.TaskName}'");
                            }

                            if (_botService.MarkQueueItemPermanentlyFailed(item.Id))
                            {
                                AppendLog(
                                    $"{logPrefix} ABANDONED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                                    $"requirement still unmet after {requirementDeferCount} retries — the prerequisite " +
                                    $"building is not built, queued or in progress. Removed from the active queue. " +
                                    $"Source='{ex.Message.Replace(Environment.NewLine, " ")}'");
                                RaiseAlarmIfQueueItemPermanentlyFailed(item, ex.Message);
                                await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
                                return true;
                            }

                            AppendLog(
                                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                                $"requirement abandon threshold reached but terminal failure could not be persisted; " +
                                $"next try in {queueWaitDelay.TotalSeconds:F0}s");
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
                            $"retryAt='{FormatQueueServerTime(retryAt)}'; later construction is held in queue order.");
                    }
                }

                if (payloadChanged)
                {
                    var payloadPersisted = PatchDeferredQueuePayload(item, updatedPayload);
                    item.Payload = updatedPayload;
                    if (IsConstructionQueueTask(item.TaskName) && !payloadPersisted)
                    {
                        AppendLog(
                            $"[construction-queue] construction payload persistence failed " +
                            $"id={item.Id} task='{item.TaskName}' " +
                            $"reason='{updatedPayload.GetValueOrDefault(BotOptionPayloadKeys.UpgradeDeferReason, "-")}'");
                    }
                }

                if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                    && TryExtractPayloadInt(ex.Message, BotOptionPayloadKeys.BuildingConstructSlotId, out var effectiveConstructSlot))
                {
                    if (BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                        && construct is not null
                        && construct.SlotId != effectiveConstructSlot)
                    {
                        var reboundPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
                        {
                            [BotOptionPayloadKeys.BuildingConstructSlotId] = effectiveConstructSlot.ToString(),
                        };
                        if (PatchDeferredQueuePayload(item, reboundPayload))
                        {
                            item.Payload = reboundPayload;
                            AppendLog(
                                $"[construct-chain] persisted effective slot {effectiveConstructSlot} " +
                                $"for {construct.Name ?? $"gid {construct.Gid}"} target level {construct.TargetLevel}.");
                        }
                    }
                    RebindPendingBuildingTemplateStep(item, effectiveConstructSlot);
                }

                if (IsConstructionQueueTask(item.TaskName))
                {
                    await TryHandleStorageCapacityDependencyAsync(item, updatedPayload);
                }

                await RefreshFarmListsUiAfterAutoSendIfNeededAsync(item, ex.Message);
                AppendLog($"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
                if (string.Equals(item.TaskName, "anti_starve_hero_crop", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("anti_starve_alarm=true", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(
                        $"ALARM: Hero crop anti-starve needs attention in village "
                        + $"'{GetQueueItemVillageName(item) ?? "-"}'. {ex.Message.Replace(Environment.NewLine, " ")}");
                }
                // A building or resource mutation can start one build and then defer because the NEXT level
                // is blocked. That deferral skips the success-path construction refresh, so the cached live
                // Travian queue can stay empty even though the worker just observed a full queue. Re-read the
                // current village's construction status (the browser is already on it) before repainting.
                if ((IsBuildingMutationTask(item.TaskName) || IsResourceUpgradeTask(item.TaskName))
                    && !isHumanizeDefer)
                {
                    try
                    {
                        await RefreshConstructionStatusAfterDeferAsync(_loopController.AcquireSessionScopeToken());
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Construction status refresh after defer skipped: {refreshEx.Message}");
                    }
                }

                if (IsConstructionQueueTask(item.TaskName)
                    && ConstructionQueueState.IsCropShortageDeferMessage(ex.Message))
                {
                    await HandleCropShortageDeferAsync(item);
                }

                // build_troops always DEFERS on its happy path: it queues troops, then returns
                // queue_wait_seconds for the cooldown. That skips the success-path troop refresh, so the
                // per-village troop-training queue cache (and the Troops B/S/W icon) stayed grey even though
                // a training queue is now active. Re-read the village's queues when troops were actually
                // queued, so the icon turns green and the state is cached (and thus persisted across restart).
                if (string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase)
                    && ex is TaskWaitException { ReasonCode: TaskWaitReasons.WorkQueued })
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

                // Tag deferred Hero state so the jitter refresh can release the task early when a live
                // signal supersedes its estimate (bucket revive, early return, sufficient HP/level-up).
                if (string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
                    && ex is TaskWaitException heroWait
                    && heroWait.ReasonCode is TaskWaitReasons.HeroReviving
                        or TaskWaitReasons.HeroAway
                        or TaskWaitReasons.HeroHpTooLow)
                {
                    var heroPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
                    {
                        [HeroDeferReasonKey] = heroWait.ReasonCode switch
                        {
                            TaskWaitReasons.HeroReviving => HeroDeferReasonReviving,
                            TaskWaitReasons.HeroAway => HeroDeferReasonAway,
                            _ => HeroDeferReasonLowHp,
                        },
                    };
                    if (_botService.UpdateDeferredQueueItem(item.Id, heroPayload))
                    {
                        item.Payload = heroPayload;
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

    private bool PatchDeferredQueuePayload(
        QueueItem item,
        Dictionary<string, string> updatedPayload,
        TimeSpan? delay = null)
    {
        var keysToRemove = item.Payload.Keys
            .Where(key => !updatedPayload.ContainsKey(key))
            .ToArray();
        return _botService.PatchDeferredQueueItem(item.Id, updatedPayload, keysToRemove, delay);
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
