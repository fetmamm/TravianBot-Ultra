using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Deferred / mid-wait refresh orchestration: deciding when a queued build or
// troop-training task is waiting on resources, scheduling a mid-wait UI refresh,
// and re-evaluating construction / troop-training waits. Extracted verbatim from
// MainWindow.xaml.cs to keep that file focused; same class, pure relocation.
public partial class MainWindow
{
    private static bool TryExtractQueueWaitDelay(string message, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = Regex.Match(message, @"queue_wait_seconds=(?<seconds>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["seconds"].Value, out var seconds))
        {
            return false;
        }

        var effectiveSeconds = Math.Max(1, seconds);
        delay = TimeSpan.FromSeconds(effectiveSeconds);
        return true;
    }

    private bool TryExtractDeferredUpgradePayload(string message, Dictionary<string, string> basePayload, out Dictionary<string, string> updatedPayload)
    {
        updatedPayload = new Dictionary<string, string>(basePayload, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var changed = false;
        foreach (var key in DeferredUpgradePayloadKeys)
        {
            var match = Regex.Match(message, $@"(?<!\S){Regex.Escape(key)}=(?<value>\S+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            updatedPayload[key] = match.Groups["value"].Value.Trim();
            changed = true;
        }

        return changed;
    }

    private static bool TryExtractPayloadInt(string? message, string key, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = Regex.Match(message, $@"(?<!\S){Regex.Escape(key)}=(?<value>\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, out value);
    }

    private void TriggerDeferredConstructionWaitRefresh(VillageStatus status, string source)
    {
        lock (_deferredConstructionRefreshSync)
        {
            if (_deferredConstructionRefreshRunning)
            {
                _pendingDeferredConstructionRefreshStatus = status;
                _pendingDeferredConstructionRefreshSource = source;
                return;
            }

            _deferredConstructionRefreshRunning = true;
        }

        RunDeferredConstructionWaitRefresh(status, source);
    }

    private void RunDeferredConstructionWaitRefresh(VillageStatus status, string source)
    {
        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshDeferredConstructionWaitsAsync(status, source);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred construction wait refresh skipped: {ex.Message}");
            }
            finally
            {
                VillageStatus? pendingStatus;
                string pendingSource;
                lock (_deferredConstructionRefreshSync)
                {
                    pendingStatus = cancellationToken.IsCancellationRequested
                        ? null
                        : _pendingDeferredConstructionRefreshStatus;
                    pendingSource = _pendingDeferredConstructionRefreshSource ?? "pending_refresh";
                    _pendingDeferredConstructionRefreshStatus = null;
                    _pendingDeferredConstructionRefreshSource = null;
                    if (pendingStatus is null)
                    {
                        _deferredConstructionRefreshRunning = false;
                    }
                }

                if (pendingStatus is not null)
                {
                    RunDeferredConstructionWaitRefresh(pendingStatus, pendingSource);
                }
            }
        });
    }

    private async Task RefreshDeferredConstructionWaitsAsync(VillageStatus status, string source)
    {
        var currentResources = ReadCurrentResourcesFromStatus(status);
        var productionByHour = ReadCurrentProductionByHourFromStatus(status);
        // Only re-evaluate deferred items for the village this status was read for. The resources read
        // belong to ONE village, so judging another village's deferred upgrade against them is wrong and
        // caused other villages' construction timers to briefly flash "Ready" (reset) then re-defer.
        var statusVillage = NormalizeVillageName(status.ActiveVillage);
        var statusVillageKey = ResolveStatusVillageKey(status);
        if (statusVillageKey is null)
        {
            AppendLog($"[construction-queue:verbose] skipped deferred refresh because village identity was ambiguous (name='{statusVillage ?? "-"}', source='{source}').");
            return;
        }

        var deferredItems = _botService
            .GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => IsConstructionQueueTask(item.TaskName))
            .Where(item => string.Equals(GetQueueItemVillageKey(item), statusVillageKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ClearConstructionLoginFillForFullSlots(status, statusVillageKey, deferredItems, source);

        var queueFullItems = deferredItems
            .Where(ConstructionQueueState.IsQueueOccupancyDeferred)
            .ToList();
        var constructionSnapshot = ConstructionQueueState.ResolveSnapshot(status);
        if (queueFullItems.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var releasableItem = queueFullItems.FirstOrDefault(item =>
                ConstructionQueueState.ResolveQueueFullRetryDelay(status, _travianPlusActive, item, now) == TimeSpan.Zero);
            if (releasableItem is not null)
            {
                if (_botService.PatchDeferredQueueItem(releasableItem.Id, null, null, TimeSpan.Zero))
                {
                    AppendLog(
                        $"[construction-queue:verbose] live status released queue-full blocker " +
                        $"id={releasableItem.Id} task='{releasableItem.TaskName}' village='{statusVillage ?? "-"}' " +
                        $"active={constructionSnapshot.ActiveCount} plus={_travianPlusActive?.ToString() ?? "unknown"} source='{source}'.");
                    AppendLog(
                        $"[construction] BUILD SLOT AVAILABLE village='{statusVillage ?? "-"}'. " +
                        $"Next queued construction will retry now.");
                }
            }
            else
            {
                var updatedCount = 0;
                var largestAdjustmentSeconds = 0d;
                foreach (var item in queueFullItems)
                {
                    var queueFullDelay = ConstructionQueueState.ResolveQueueFullRetryDelay(
                        status,
                        _travianPlusActive,
                        item,
                        now);
                    if (queueFullDelay is null || queueFullDelay <= TimeSpan.Zero)
                    {
                        continue;
                    }

                    var remainingSeconds = Math.Max(0, (int)Math.Ceiling((item.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));
                    var adjustmentSeconds = Math.Abs(remainingSeconds - queueFullDelay.Value.TotalSeconds);
                    if (adjustmentSeconds <= 5)
                    {
                        continue;
                    }

                    if (_botService.PatchDeferredQueueItem(item.Id, null, null, queueFullDelay.Value))
                    {
                        updatedCount++;
                        largestAdjustmentSeconds = Math.Max(largestAdjustmentSeconds, adjustmentSeconds);
                    }
                }

                if (updatedCount > 0 && largestAdjustmentSeconds >= 60)
                {
                    AppendLog(
                        $"[construction-queue:verbose] live status synchronized {updatedCount} queue-full blocker(s) " +
                        $"village='{statusVillage ?? "-"}' active={constructionSnapshot.ActiveCount} " +
                        $"plus={_travianPlusActive?.ToString() ?? "unknown"} adjustmentSeconds={largestAdjustmentSeconds:F0} " +
                        $"source='{source}'.");
                }
            }
        }

        foreach (var item in deferredItems)
        {
            if (ConstructionQueueState.ResolveDeferReason(item) == ConstructionDeferReason.StorageCapacity)
            {
                var block = ResolveStorageCapacityBlock(item.Payload, status, preferLiveStatus: true)
                    ?? ResolveExplicitStorageCapacityBlock(item.Payload, status);
                if (block is null
                    && _botService.PatchDeferredQueueItem(item.Id, null, null, TimeSpan.Zero))
                {
                    AppendLog(
                        $"Deferred upgrade resumed from {source}: storage capacity now satisfies {DescribeDeferredUpgrade(item.Payload)}.");
                }

                continue;
            }

            // Only resource deferrals may be resumed from a resource snapshot. Queue occupancy,
            // already-running targets, missing requirements and generic retries have different owners.
            if (ConstructionQueueState.ResolveDeferReason(item) != ConstructionDeferReason.Resources)
            {
                continue;
            }

            if (!TryReadDeferredUpgradeRequirements(item.Payload, out var required))
            {
                // Requirement-less resource defer: the worker could only read Travian's "enough resources
                // in HH:MM:SS" page timer, not the upgrade cost (no upgrade_required_* in the payload), so
                // the resume math below cannot run. Normally we trust that timer — but it is a snapshot. If
                // the village's resources are now FULL (a hero/farm/NPC drop topped it off after the timer
                // was captured), the build is almost certainly affordable now and the cached wait is stale,
                // so the village would idle out a countdown that no longer applies. Resume so the worker
                // re-checks the live build page instead. A full village that still cannot afford the upgrade
                // reclassifies as storage_capacity (different reason), so this cannot spin in a retry loop.
                if (IsVillageResourcesFull(status, currentResources)
                    && (item.NextAttemptAt - DateTimeOffset.UtcNow) > TimeSpan.FromSeconds(5)
                    && _botService.PatchDeferredQueueItem(item.Id, null, null, TimeSpan.Zero))
                {
                    AppendLog(
                        $"Deferred upgrade resumed from {source}: {DescribeDeferredUpgrade(item.Payload)} — "
                        + "village resources full, cached page-timer wait was stale; re-checking now.");
                }

                continue;
            }

            var evaluation = EvaluateDeferredUpgradeWait(item.Payload, required, currentResources, productionByHour);
            var updatedPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
            WriteDeferredUpgradeRuntimeValues(updatedPayload, currentResources, productionByHour, evaluation);
            var remainingSeconds = Math.Max(0, (int)Math.Ceiling((item.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));

            if (evaluation.ResourcesEnough)
            {
                if (remainingSeconds <= 1)
                {
                    continue;
                }

                var changed = PatchDeferredQueuePayload(item, updatedPayload, TimeSpan.Zero);
                if (changed)
                {
                    AppendLog($"Deferred upgrade resumed from {source}: {DescribeDeferredUpgrade(item.Payload)} now has enough resources.");
                }
                continue;
            }

            if (Math.Abs(remainingSeconds - evaluation.WaitSeconds) <= 5)
            {
                continue;
            }

            var delay = TimeSpan.FromSeconds(evaluation.WaitSeconds);
            var updated = PatchDeferredQueuePayload(item, updatedPayload, delay);
            if (updated)
            {
                AppendLog($"Deferred upgrade wait updated from {source}: {DescribeDeferredUpgrade(item.Payload)} wait={evaluation.WaitSeconds}s reason={evaluation.WaitReason}.");
            }
        }

        await Dispatcher.InvokeAsync(() => RefreshQueueUi());
    }

    // Makes every pending (deferred) construction item ready to retry right now. Used when the user
    // changes a setting that could let a blocked upgrade proceed immediately (e.g. enabling hero
    // resource transfer, or toggling the Construction group off then on) so the loop re-reads
    // instead of waiting out the existing timer.
    private void ResetDeferredConstructionWaitsNow(string source)
    {
        var items = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => IsConstructionQueueTask(item.TaskName))
            .ToList();

        var resetCount = 0;
        foreach (var item in items)
        {
            if (_botService.PatchDeferredQueueItem(item.Id, null, null, TimeSpan.Zero))
            {
                resetCount++;
            }
        }

        if (resetCount > 0)
        {
            AppendLog($"Construction waits reset ({source}): {resetCount} item(s) will retry now.");
        }

        RefreshQueueUi();
    }

    // A fresh login represents a new player visit. Ready, queue-full and humanize-waiting rows get
    // one attempt to fill available Travian slots immediately. Resource, prerequisite and storage
    // waits keep their authoritative deadlines.
    private void PrepareConstructionLoginFill()
    {
        var now = DateTimeOffset.UtcNow;
        if (!LoadBotOptions().ConstructionHumanizeDelayEnabled)
        {
            ApplyConstructionHumanizeToggleTransition(enabled: false);
            var released = 0;
            foreach (var item in _botService.GetQueueItemsForDisplay()
                         .Where(item => item.Status == QueueStatus.Pending)
                         .Where(item => IsConstructionQueueTask(item.TaskName))
                         .Where(IsQueueItemAllowedByAutomationSettings)
                         .Where(ConstructionQueueState.IsQueueOccupancyDeferred))
            {
                if (_botService.PatchDeferredQueueItem(
                        item.Id,
                        null,
                        [
                            BotOptionPayloadKeys.ConstructionLoginFill,
                            BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
                        ],
                        TimeSpan.Zero))
                {
                    released++;
                }
            }

            if (released > 0)
            {
                AppendLog($"[construction-login-fill] released {released} stale queue-full row(s) for immediate live validation while humanization is disabled.");
                Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
                RefreshQueueUi();
            }
            return;
        }

        var prepared = 0;
        var expiresAt = now.AddMinutes(PacingDefaults.ConstructionLoginFillWindowMinutes).ToUnixTimeSeconds();
        foreach (var item in _botService.GetQueueItemsForDisplay()
                     .Where(item => item.Status == QueueStatus.Pending)
                     .Where(item => IsConstructionQueueTask(item.TaskName))
                     .Where(IsQueueItemAllowedByAutomationSettings))
        {
            var isHumanizeWait = ConstructionQueueState.IsConstructionHumanizeDeferred(item);
            var isQueueWait = ConstructionQueueState.IsQueueOccupancyDeferred(item);
            if (!ConstructionQueueState.ShouldPrepareLoginFill(item, now))
            {
                continue;
            }

            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.ConstructionLoginFill] = "true",
                [BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds] = expiresAt.ToString(),
            };
            payload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
            payload.Remove(BotOptionPayloadKeys.QueueHumanizeExtraSeconds);

            var valuesToSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.ConstructionLoginFill] = "true",
                [BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds] = expiresAt.ToString(),
            };
            string[] keysToRemove =
            [
                BotOptionPayloadKeys.ConstructionPreSleepFill,
                BotOptionPayloadKeys.QueueHumanizeExtraSeconds,
            ];
            var delay = isHumanizeWait || isQueueWait ? TimeSpan.Zero : (TimeSpan?)null;
            var updated = _botService.PatchDeferredQueueItem(item.Id, valuesToSet, keysToRemove, delay);
            if (!updated)
            {
                AppendLog($"[construction-login-fill] could not prepare id={item.Id} task='{item.TaskName}'.");
                continue;
            }

            item.Payload = payload;
            prepared++;
        }

        if (prepared > 0)
        {
            AppendLog($"[construction-login-fill] prepared {prepared} construction row(s) to fill available slots without construction start delay.");
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            RefreshQueueUi();
        }
    }

    // Once a category is full, the login burst is complete for every later row competing for that
    // category. Removing the flag here ensures the next online timer completion is humanized normally.
    private void ClearConstructionLoginFillForFullSlots(
        VillageStatus status,
        string? statusVillageKey,
        IEnumerable<QueueItem>? candidates = null,
        string source = "live status")
    {
        var cleared = 0;
        var items = candidates ?? _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => IsConstructionQueueTask(item.TaskName));
        foreach (var item in items
                     .Where(item => item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionLoginFill))
                     .Where(item => statusVillageKey is not null
                         && string.Equals(GetQueueItemVillageKey(item), statusVillageKey, StringComparison.OrdinalIgnoreCase)))
        {
            if (ConstructionQueueState.ResolveAvailabilityForItem(status, _travianPlusActive, item)
                != ConstructionQueueAvailability.Full)
            {
                continue;
            }

            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
            payload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
            payload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);
            if (_botService.PatchDeferredQueueItem(
                    item.Id,
                    null,
                    [
                        BotOptionPayloadKeys.ConstructionLoginFill,
                        BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
                    ]))
            {
                item.Payload = payload;
                cleared++;
            }
        }

        if (cleared > 0)
        {
            AppendLog($"[construction-login-fill] completed for {cleared} row(s): live construction category is full (source='{source}').");
        }
    }

    private string? ResolveStatusVillageKey(VillageStatus status)
    {
        if (status.ActiveVillageCoordX.HasValue && status.ActiveVillageCoordY.HasValue)
        {
            return _villageSettingsStore.ResolveCanonicalKey(VillageKey.FromCoords(
                status.ActiveVillageCoordX.Value,
                status.ActiveVillageCoordY.Value));
        }

        var activeName = NormalizeVillageName(status.ActiveVillage);
        if (activeName is null)
        {
            return null;
        }

        var statusMatches = status.Villages
            .Where(village => string.Equals(NormalizeVillageName(village.Name), activeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (statusMatches.Count == 1)
        {
            var village = statusMatches[0];
            var key = GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name);
            return _villageSettingsStore.ResolveCanonicalKey(key);
        }

        return _villageStatusCache.TryGetUniqueKeyByName(activeName, out var cachedKey)
            ? _villageSettingsStore.ResolveCanonicalKey(cachedKey)
            : null;
    }

    private void ResetConstructionBuildQueueTimerForManualRefresh()
    {
        _continuousLoopConstructionStatusNeedsSync = true;
        _buildQueueReachedZeroPendingCompletion = _buildQueueActiveCount > 0;

        UpdateBuildQueueStatusText();
        UpdateAutomationLoopRunningIndicators();
        AppendLog("Construction group re-enabled. Fresh Travian queue status requested; cached queue preserved until confirmed.");
    }

    private void TriggerDeferredTroopTrainingWaitRefresh(VillageStatus status, string source, bool force = false)
    {
        if (!force && status.Resources.Count == 0)
        {
            return;
        }

        if (_deferredTroopTrainingRefreshRunning)
        {
            _pendingDeferredTroopTrainingRefreshStatus = status;
            _pendingDeferredTroopTrainingRefreshSource = source;
            return;
        }

        _deferredTroopTrainingRefreshRunning = true;
        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshDeferredTroopTrainingWaitsAsync(status, source);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred troop training wait refresh skipped: {ex.Message}");
            }
            finally
            {
                _deferredTroopTrainingRefreshRunning = false;
                if (!cancellationToken.IsCancellationRequested
                    && _pendingDeferredTroopTrainingRefreshStatus is not null)
                {
                    var pendingStatus = _pendingDeferredTroopTrainingRefreshStatus;
                    var pendingSource = _pendingDeferredTroopTrainingRefreshSource ?? "pending_refresh";
                    _pendingDeferredTroopTrainingRefreshStatus = null;
                    _pendingDeferredTroopTrainingRefreshSource = null;
                    TriggerDeferredTroopTrainingWaitRefresh(pendingStatus, pendingSource, force: true);
                }
            }
        });
    }

    private async Task RefreshDeferredTroopTrainingWaitsAsync(VillageStatus status, string source)
    {
        var currentResources = ReadCurrentResourcesFromStatus(status);
        var productionByHour = ReadCurrentProductionByHourFromStatus(status);
        var warehouseCapacity = status.WarehouseCapacity;
        var granaryCapacity = status.GranaryCapacity;
        if (warehouseCapacity is not > 0 || granaryCapacity is not > 0)
        {
            return;
        }

        var baseOptions = ApplySelectedVillageToOptions(LoadBotOptions());
        // Only re-evaluate items for the village this status was read for; the resources belong to ONE
        // village, so judging another village's deferred item against them is wrong (mirrors the
        // construction refresh).
        var statusVillage = NormalizeVillageName(status.ActiveVillage);
        var statusVillageKey = ResolveStatusVillageKey(status);
        if (statusVillageKey is null)
        {
            AppendLog($"[troop-training:verbose] skipped deferred refresh because village identity was ambiguous (name='{statusVillage ?? "-"}', source='{source}').");
            return;
        }
        var deferredItems = _botService
            .GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(GetQueueItemVillageKey(item), statusVillageKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deferredItems.Count == 0)
        {
            return;
        }

        var knownBuildings = status.Buildings ?? [];
        foreach (var item in deferredItems)
        {
            // Build the threshold/run-mode from the item's OWN per-village snapshot (the loop injects the
            // village override into the payload), not the global config — otherwise a per-village % limit
            // is judged against the account-wide default.
            var itemOptions = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);
            var fallbackCooldownSeconds = ResolveTroopTrainingFallbackCooldownSeconds(itemOptions.TroopTrainingFallbackCooldownSeconds);
            var requests = BuildDeferredTroopTrainingRequests(itemOptions);
            var evaluation = EvaluateDeferredTroopTrainingWait(
                requests,
                knownBuildings,
                currentResources,
                productionByHour,
                warehouseCapacity.Value,
                granaryCapacity.Value,
                fallbackCooldownSeconds);
            var remainingSeconds = Math.Max(0, (int)Math.Ceiling((item.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));
            if (string.Equals(evaluation.WaitReason, "skip_refresh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (evaluation.Ready)
            {
                if (remainingSeconds <= 1)
                {
                    continue;
                }

                if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.Zero))
                {
                    AppendLog($"Deferred troop training resumed from {source}: resources now satisfy a % limit.");
                }

                continue;
            }

            if (Math.Abs(remainingSeconds - evaluation.WaitSeconds) <= 5)
            {
                continue;
            }

            if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.FromSeconds(evaluation.WaitSeconds)))
            {
                AppendLog($"Deferred troop training wait updated from {source}: wait={evaluation.WaitSeconds}s reason={evaluation.WaitReason}.");
            }
        }

        await Dispatcher.InvokeAsync(() => RefreshQueueUi());
    }

    // Post-defer construction refresh: the build task just reloaded dorf2, so read storage + build
    // queue from the CURRENT page (no navigation) and merge the construction data into the caches.
    // The old full-status refresh navigated dorf1+dorf2 for data a building mutation never changes
    // (resource fields/production). Falls back to the full read only when the quick read fails.
    private async Task RefreshConstructionStatusAfterDeferAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
            await RefreshCurrentPageStorageStatusAsync(options, "construction_defer_quick", cancellationToken);
            AppendLog("[construction-refresh] current-page refresh used for deferred construction; skipped full dorf1+dorf2 read.");
        }
        catch (Exception ex)
        {
            AppendLog($"[construction-refresh] current-page defer refresh failed ({ex.Message}); falling back to full construction status.");
            await RefreshConstructionStatusAsync(cancellationToken);
        }
    }

    private async Task RefreshConstructionStatusAsync(CancellationToken cancellationToken)
    {
        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions());
        var status = await ReadVillageStatusWithRetryAsync(
            options,
            cancellationToken,
            resourceOnly: false,
            forceCurrentVillage: true);
        await Dispatcher.InvokeAsync(() =>
        {
            SetActiveWorkingVillageFromStatus(status);
            CacheVillageStatus(status);
            ReconcilePendingBuildingQueueWithLiveStatus(status);
            if (!IsStatusForSelectedVillage(status))
            {
                return;
            }

            _lastBuildingStatus = status;
            ApplyVillageStatusToUi(status);
            PopulateBuildingsTab(status);
        });
    }

    private static bool NeedsConstructionStatusRefresh(string taskName)
    {
        return IsResourceUpgradeTask(taskName)
            || IsBuildingMutationTask(taskName);
    }

    private static bool IsConstructionQueueTask(string taskName)
    {
        return IsResourceUpgradeTask(taskName)
            || IsBuildingMutationTask(taskName);
    }

}
