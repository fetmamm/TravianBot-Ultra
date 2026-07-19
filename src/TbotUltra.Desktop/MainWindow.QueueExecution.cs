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
        if (status is null)
        {
            return (null, []);
        }

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

    private async Task<bool> TryHandleConstructRequirementPreRunGuardAsync(
        QueueItem item,
        string logPrefix,
        Stopwatch timer)
    {
        var result = ResolveConstructRequirementGuardForQueueItem(item, DateTimeOffset.UtcNow);
        if (result.Action == ConstructionRequirementGuardAction.None)
        {
            return false;
        }

        if (result.Action is ConstructionRequirementGuardAction.DeferForQueuedPrerequisite
            or ConstructionRequirementGuardAction.FailMissingPrerequisite)
        {
            if (await TryHandleConstructRequirementRepairAsync(item, result, logPrefix, timer))
            {
                return true;
            }
        }

        if (result.Action is ConstructionRequirementGuardAction.DeferForActivePrerequisite
            or ConstructionRequirementGuardAction.DeferForQueuedPrerequisite)
        {
            var delay = result.Delay ?? TimeSpan.FromSeconds(60);
            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
                [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                    ConstructionQueueState.CurrentDeferClassificationVersion,
            };
            payload.Remove(BotOptionPayloadKeys.RequirementDeferCount);
            payload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
            payload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
            payload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);

            if (!_botService.MarkQueueItemDeferred(item.Id, delay))
            {
                AppendLog(
                    $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                    "construct prerequisite wait detected, but defer could not be persisted before worker execution");
                return false;
            }

            if (_botService.PatchDeferredQueueItem(
                    item.Id,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
                        [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] = ConstructionQueueState.CurrentDeferClassificationVersion,
                    },
                    [
                        BotOptionPayloadKeys.RequirementDeferCount,
                        BotOptionPayloadKeys.ConstructionPreSleepFill,
                        BotOptionPayloadKeys.ConstructionLoginFill,
                        BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
                    ]))
            {
                item.Payload = payload;
            }
            else
            {
                AppendLog(
                    $"[construction-dependency] prerequisite defer payload persistence failed " +
                    $"id={item.Id} task='{item.TaskName}'");
            }

            var source = result.Action == ConstructionRequirementGuardAction.DeferForActivePrerequisite
                ? "active prerequisite"
                : "queued prerequisite";
            AppendLog(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                $"construct requirements waiting for {source}: {result.Detail}. " +
                $"Next try in {delay.TotalSeconds:F0}s; worker was not started.");
            await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
            return true;
        }

        if (_botService.MarkQueueItemPermanentlyFailed(item.Id))
        {
            var message =
                $"construct requirements missing with no same-village queued or active prerequisite: {result.Detail}";
            AppendLog(
                $"{logPrefix} ABANDONED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                $"{message}. Removed from the active queue before worker execution.");
            RaiseAlarmIfQueueItemPermanentlyFailed(item, message);
            await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
            return true;
        }

        AppendLog(
            $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
            $"construct requirements missing ({result.Detail}) but terminal failure could not be persisted");
        return false;
    }

    private async Task<bool> TryHandleConstructRequirementRepairAsync(
        QueueItem item,
        ConstructionRequirementGuardResult guardResult,
        string logPrefix,
        Stopwatch timer)
    {
        var context = ResolveConstructRequirementContextForQueueItem(item);
        if (context.Status is null)
        {
            return false;
        }

        var plan = ConstructionRequirementRepairPlanner.Plan(
            item,
            context.Status,
            context.SameVillageItems,
            DateTimeOffset.UtcNow);
        if (plan.HasBlockers)
        {
            AppendLog(
                $"[construction-repair] cannot repair construct requirements for id={item.Id}: {plan.Detail}");
            return false;
        }

        if (!plan.HasSteps)
        {
            return false;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        var maxPriority = queueItems.Select(entry => entry.Priority).DefaultIfEmpty(item.Priority).Max();
        var firstPriority = maxPriority > int.MaxValue - plan.Steps.Count
            ? int.MaxValue
            : maxPriority + plan.Steps.Count;
        var changedIds = new List<Guid>();
        var created = 0;
        var promoted = 0;

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var priority = firstPriority == int.MaxValue
                ? int.MaxValue - index
                : firstPriority - index;
            var payload = BuildConstructionRequirementRepairPayload(
                item,
                step,
                markAsAutomaticRepair: step.Kind == ConstructionRequirementRepairStepKind.Enqueue);

            if (step.Kind == ConstructionRequirementRepairStepKind.Promote
                && step.ExistingQueueItemId is Guid existingId)
            {
                var existing = queueItems.FirstOrDefault(entry => entry.Id == existingId);
                if (existing?.Status != QueueStatus.Pending)
                {
                    AppendLog(
                        $"[construction-repair] skipped promote id={existingId}: item is {existing?.Status.ToString() ?? "missing"}.");
                    continue;
                }

                if (_botService.UpdatePendingQueueItem(existingId, payload, priority, TimeSpan.Zero))
                {
                    changedIds.Add(existingId);
                    promoted++;
                    AppendLog(
                        $"[construction-repair] promoted queued repair id={existingId} priority={priority}: {step.Reason}.");
                }
                else
                {
                    AppendLog(
                        $"[construction-repair] failed to promote queued repair id={existingId}: {step.Reason}.");
                }

                continue;
            }

            var repairItem = _botService.Enqueue(step.TaskName, payload, priority, maxRetries: 3);
            changedIds.Add(repairItem.Id);
            created++;
            AppendLog(
                $"[construction-repair] queued automatic repair id={repairItem.Id} priority={priority}: {step.Reason}.");
        }

        if (changedIds.Count == 0)
        {
            return false;
        }

        var parentPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                ConstructionQueueState.CurrentDeferClassificationVersion,
        };
        parentPayload.Remove(BotOptionPayloadKeys.RequirementDeferCount);
        parentPayload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
        parentPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
        parentPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);

        var parentDelay = guardResult.Delay ?? TimeSpan.FromSeconds(60);
        if (!_botService.MarkQueueItemDeferred(item.Id, parentDelay))
        {
            AppendLog(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                "automatic construct requirement repair was queued, but parent defer could not be persisted");
            return false;
        }

        if (_botService.PatchDeferredQueueItem(
                item.Id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
                    [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] = ConstructionQueueState.CurrentDeferClassificationVersion,
                },
                [
                    BotOptionPayloadKeys.RequirementDeferCount,
                    BotOptionPayloadKeys.ConstructionPreSleepFill,
                    BotOptionPayloadKeys.ConstructionLoginFill,
                    BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
                ]))
        {
            item.Payload = parentPayload;
        }
        else
        {
            AppendLog(
                $"[construction-repair] parent defer payload persistence failed id={item.Id} task='{item.TaskName}'.");
        }

        RequestQueueUiRefresh(selectId: changedIds[0]);
        await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
        AppendLog(
            $"{logPrefix} REPAIR {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
            $"requirements '{guardResult.Detail}' missing; automatic repair queued/promoted " +
            $"created={created}, promoted={promoted}. Parent retries in {parentDelay.TotalSeconds:F0}s.");
        return true;
    }

    internal static Dictionary<string, string> BuildConstructionRequirementRepairPayload(
        QueueItem parent,
        ConstructionRequirementRepairStep step,
        bool markAsAutomaticRepair)
    {
        var payload = new Dictionary<string, string>(step.Payload, StringComparer.OrdinalIgnoreCase);
        if (markAsAutomaticRepair)
        {
            payload[BotOptionPayloadKeys.AutoAddedBy] =
                BotOptionPayloadKeys.AutoAddedByConstructionRequirementRepair;
            payload[BotOptionPayloadKeys.AutoAddedParentId] = parent.Id.ToString();
            payload[BotOptionPayloadKeys.AutoAddedReason] = step.Reason;
            payload[BotOptionPayloadKeys.AutoAddedRequirement] = step.RequirementText;
        }

        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageName);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageUrl);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageKey);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.NpcTradeEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterMinBuildMinutes);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterRandomEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterRandomChancePercent);
        return payload;
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
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
        MarkDueConstructionForPreSleepFill(item);
        RefreshConstructFasterPayloadForExecution(item);
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
            var constructGuardCanUseCache =
                await TryRefreshConstructTargetVillageStatusBeforeGuardAsync(item, options, cancellationToken);
            if (constructGuardCanUseCache
                && await TryHandleConstructQueueFullBeforeRequirementGuardAsync(item, logPrefix, tickSw))
            {
                freshBuildingsRefreshDone = true;
                return true;
            }

            if (constructGuardCanUseCache
                && await TryHandleConstructRequirementPreRunGuardAsync(item, logPrefix, tickSw))
            {
                freshBuildingsRefreshDone = true;
                return true;
            }

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
            MarkNetworkConnectionHealthy();
            if (mode == QueueExecutionMode.ContinuousLoop)
            {
                _ = Dispatcher.BeginInvoke(() => LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _botService.MarkQueueItemDeferred(item.Id, TimeSpan.Zero);
            AppendLog($"{logPrefix} PAUSED {tickSw.Elapsed.TotalSeconds:F1}s task={item.TaskName} | queued item kept for retry");
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
            if (!cancellationToken.IsCancellationRequested
                && mode == QueueExecutionMode.AutoQueue
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

    private async Task<bool> TryHandleConstructQueueFullBeforeRequirementGuardAsync(
        QueueItem item,
        string logPrefix,
        Stopwatch timer)
    {
        if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var status = ResolveBuildingStatusForQueueItem(item);
        if (status is null
            || ConstructionQueueState.ResolveAvailabilityForItem(status, _travianPlusActive, item)
                != ConstructionQueueAvailability.Full)
        {
            return false;
        }

        ClearConstructionLoginFillForFullSlots(
            status,
            GetQueueItemVillageKey(item),
            source: "construction preflight");

        var now = DateTimeOffset.UtcNow;
        var delay = ConstructionQueueState.ResolveQueueFullRetryDelay(status, _travianPlusActive, item, now)
            ?? TimeSpan.FromSeconds(60);
        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
            [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                ConstructionQueueState.CurrentDeferClassificationVersion,
        };
        payload.Remove(BotOptionPayloadKeys.RequirementDeferCount);
        payload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
        payload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);

        if (!_botService.MarkQueueItemDeferred(item.Id, delay))
        {
            AppendLog(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                "live full build queue was detected before requirement repair, but defer could not be persisted");
            return false;
        }

        if (_botService.PatchDeferredQueueItem(
                item.Id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
                    [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] = ConstructionQueueState.CurrentDeferClassificationVersion,
                },
                [
                    BotOptionPayloadKeys.RequirementDeferCount,
                    BotOptionPayloadKeys.ConstructionLoginFill,
                    BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
                ],
                delay))
        {
            item.Payload = payload;
        }
        else
        {
            AppendLog(
                $"[construction-queue] preflight queue-full payload persistence failed " +
                $"id={item.Id} task='{item.TaskName}'");
        }

        var villageName = NormalizeVillageName(GetQueueItemVillageName(item)) ?? status.ActiveVillage ?? "-";
        var retryAt = now + delay;
        var activeCount = ConstructionQueueState.ResolveCurrentActiveConstructions(status, now).Count;
        AppendLog(
            $"[construction-preflight] stopped before requirement repair " +
            $"id={item.Id} village='{villageName}' active={activeCount} " +
            $"waitSeconds={delay.TotalSeconds:F0}; queue was not modified.");
        AppendLog(
            $"[construction] BUILD QUEUE FULL village='{villageName}'. " +
            $"Construction order is held until the first active construction finishes. " +
            $"Next retry: {FormatQueueServerTime(retryAt)} (in {delay.TotalSeconds:F0}s).");
        await Dispatcher.InvokeAsync(RefreshVillageActivityIndicatorsOnDashboard);
        return true;
    }

    private async Task<bool> TryRefreshConstructTargetVillageStatusBeforeGuardAsync(
        QueueItem item,
        BotOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var targetVillageName = NormalizeVillageName(GetQueueItemVillageName(item));
        var targetVillageUrl = GetQueueItemPayloadValue(item, BotOptionPayloadKeys.TargetVillageUrl);
        if (targetVillageName is null && string.IsNullOrWhiteSpace(targetVillageUrl))
        {
            return true;
        }

        try
        {
            AppendLog(
                $"[construction-preflight] reading live dorf1/dorf2 for construct target village " +
                $"'{targetVillageName ?? targetVillageUrl}' before requirement guard.");
            var status = await _botService.ReadVillageStatusAsync(
                options,
                AppendLog,
                targetVillageName,
                targetVillageUrl,
                cancellationToken);
            await Dispatcher.InvokeAsync(() => CacheVillageStatus(status, targetVillageName));
            AppendLog(
                $"[construction-preflight] cached live target village '{targetVillageName ?? status.ActiveVillage}': " +
                $"fields={status.ResourceFields.Count}, buildings={status.Buildings.Count}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[construction-preflight] live target village read failed before construct guard: {ex.Message}. " +
                "Skipping cached requirement guard; worker will validate the live construct page.");
            return false;
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

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && TryExtractPayloadInt(
                executionResult.LastTask?.Message,
                BotOptionPayloadKeys.BuildingConstructSlotId,
                out var effectiveConstructSlot))
        {
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
            var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(item.TaskName, terminalCountBefore);
            if (!fastUpdated)
            {
                try
                {
                    resourceStatusRead = await RefreshResourceSnapshotForUiAsync(
                        options,
                        cancellationToken,
                        currentPageOnly: true) is not null;
                    if (resourceStatusRead)
                    {
                        AppendLog("[resource-refresh] current-page snapshot used after resource task; selected village was not opened.");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[resource-refresh] current-page snapshot after resource task skipped ({ex.Message}); storage-only refresh will retry without village navigation.");
                }
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
            if (!resourceStatusRead)
            {
                await RefreshCurrentPageStorageStatusAsync(options, "construction_success", cancellationToken);
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

    private async Task<(bool FullStatusRead, bool StorageStatusRead)> RefreshConstructionStatusAfterBuildingMutationAsync(
        BotOptions options,
        BotTaskExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var outcome = executionResult.LastTask?.ConstructionOutcome ?? ConstructionTaskOutcome.UnknownSuccess;
        // A full refresh means leaving the page the task ended on to read dorf1 AND dorf2. That is only
        // worth it when the village actually changed. QueuedOrInProgress changed the build queue but the
        // levels are still readable from the current page; AlreadySatisfied/AlreadyExists changed nothing
        // at all (the building was already at the target level), so re-reading both overviews just costs a
        // pointless dorf2 -> dorf1 -> dorf2 round trip before the next queue item runs.
        if (outcome is ConstructionTaskOutcome.QueuedOrInProgress
            or ConstructionTaskOutcome.AlreadySatisfied
            or ConstructionTaskOutcome.AlreadyExists)
        {
            try
            {
                await RefreshCurrentPageStorageStatusAsync(options, "construction_success_quick", cancellationToken);
                AppendLog(
                    $"[construction-refresh] current-page refresh used for {outcome}; skipped full dorf1+dorf2 read.");
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
        if (ex is AccountAccessException accessException)
        {
            _botService.MarkQueueItemDeferred(item.Id, TimeSpan.Zero);
            await HoldAccountAutomationAsync(accessException);
            AppendLog(
                $"{logPrefix} STOPPED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                "account requires manual review; queued item kept");
            return false;
        }

        if (IsTransientConnectionFailure(ex))
        {
            var retryDelay = NextTransientNavigationRetryDelay();
            MarkTransientNetworkUnavailable(retryDelay);
            if (_botService.MarkQueueItemDeferred(item.Id, retryDelay))
            {
                AppendLog(
                    $"{logPrefix} TRANSIENT {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                    $"slow/unavailable page; safe retry in {retryDelay.TotalSeconds:F0}s without consuming retries");
                return true;
            }
        }

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

        if (ex is UnexpectedTravianLanguageException languageException)
        {
            _botService.MarkQueueItemDeferred(item.Id, TimeSpan.Zero);
            AppendLog(
                $"{logPrefix} PAUSED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | " +
                "Travian language must be English before automation can continue.");
            await HandleUnexpectedTravianLanguageAsync(languageException);
            return false;
        }

        if (await TryHandleTroopsBlockedExecutionAsync(item, ex, logPrefix))
        {
            return true;
        }

        if (TryHandleTownHallUnavailableExecution(item, ex, logPrefix))
        {
            return true;
        }

        // Cross-thread UI access (a background runner touching a WPF control) fails a task instantly and,
        // for maxRetries=0 runtime items, would re-queue it every tick — spamming the loop. Don't stop the
        // runner: defer the offending task for 30 min so it retries later, and raise an alarm so the user
        // sees something is wrong. NOTE: the raw "the calling thread cannot access this object..." text is
        // deliberately kept OUT of the alarm line — IsAlarmMessage auto-acknowledges that phrase, which
        // would hide it from the (red) alarm list.
        if (ex is InvalidOperationException ioe
            && ioe.Message.Contains("different thread owns it", StringComparison.OrdinalIgnoreCase))
        {
            var uiThreadRetryDelay = TimeSpan.FromMinutes(30);
            if (_botService.MarkQueueItemDeferred(item.Id, uiThreadRetryDelay))
            {
                AppendLog(
                    $"ALARM: task '{item.TaskName}' hit a UI-thread access error " +
                    $"({logPrefix}, {timer.Elapsed.TotalSeconds:F1}s). Deferred " +
                    $"{uiThreadRetryDelay.TotalMinutes:F0} min and will retry — something is wrong, please check.");
                return true;
            }

            // Defer could not be persisted: fall through to the normal failure handling below rather than
            // silently swallowing the error.
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
                                            : ConstructionQueueState.IsConstructionHumanizeDeferMessage(ex.Message)
                                                ? BotOptionPayloadKeys.UpgradeDeferReasonHumanize
                                                : BotOptionPayloadKeys.UpgradeDeferReasonRetry;
                    updatedPayload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
                        ConstructionQueueState.CurrentDeferClassificationVersion;
                    payloadChanged = true;

                    // The pre-sleep fill flag is valid for exactly one execution attempt — this attempt
                    // just ran, so drop it. The sweep re-flags the item if it defers into the window again.
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
                    // Login fill lasts only while starts can make progress. Any defer ends the burst;
                    // later retries use the normal online-completion humanize rules.
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
                    updatedPayload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);

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
                    RebindPendingBuildingTemplateStep(item, effectiveConstructSlot);
                }

                if (IsConstructionQueueTask(item.TaskName))
                {
                    await TryHandleStorageCapacityDependencyAsync(item, updatedPayload);
                }

                await RefreshFarmListsUiAfterAutoSendIfNeededAsync(item, ex.Message);
                AppendLog($"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s{constructionSuffix}");
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
