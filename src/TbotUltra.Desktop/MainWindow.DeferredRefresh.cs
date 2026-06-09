using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

// Deferred / mid-wait refresh orchestration: deciding when a queued build or
// troop-training task is waiting on resources, scheduling a mid-wait UI refresh,
// and re-evaluating construction / troop-training waits. Extracted verbatim from
// MainWindow.xaml.cs to keep that file focused; same class, pure relocation.
public partial class MainWindow
{
    private bool IsBuildingUpgradeQueueTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResourceAwareQueueTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "build_troops", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleDeferredBuildingsMidWaitRefresh(QueueItem item, TimeSpan queueWaitDelay)
    {
        if (!IsBuildingUpgradeQueueTask(item.TaskName) || queueWaitDelay.TotalSeconds < 3)
        {
            return;
        }

        var halfDelay = TimeSpan.FromSeconds(Math.Max(1, Math.Floor(queueWaitDelay.TotalSeconds / 2d)));
        var baseOptions = ApplySelectedVillageToOptions(LoadBotOptions());
        var itemOptions = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);

        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                await Task.Delay(halfDelay, cancellationToken);
                var status = await _botService.ReadBuildingsStatusAsync(itemOptions, AppendLog, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    _lastBuildingStatus = status;
                    PopulateBuildingsTab(status);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred dorf2 refresh skipped: {ex.Message}");
            }
        });
    }

    private void ScheduleDeferredResourcesMidWaitRefresh(QueueItem item, TimeSpan queueWaitDelay)
    {
        if (!IsResourceAwareQueueTask(item.TaskName) || queueWaitDelay.TotalSeconds < 10)
        {
            return;
        }

        var baseOptions = ApplySelectedVillageToOptions(LoadBotOptions());
        var itemOptions = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);
        var deadlineUtc = DateTimeOffset.UtcNow + queueWaitDelay;

        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                while (DateTimeOffset.UtcNow < deadlineUtc && IsQueueItemStillDeferred(item.Id))
                {
                    var remaining = deadlineUtc - DateTimeOffset.UtcNow;
                    var delaySeconds = Math.Clamp(
                        Math.Floor(remaining.TotalSeconds / 2d),
                        1d,
                        120d);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

                    if (!IsQueueItemStillDeferred(item.Id))
                    {
                        return;
                    }

                    await RefreshCurrentPageStorageStatusAsync(
                        itemOptions,
                        "deferred_queue_wait",
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred storage refresh skipped: {ex.Message}");
            }
        });
    }

    private bool IsQueueItemStillDeferred(Guid itemId)
    {
        try
        {
            var item = _botService.GetQueueItemsForDisplay().FirstOrDefault(candidate => candidate.Id == itemId);
            return item is { Status: QueueStatus.Pending } && item.NextAttemptAt > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

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

    private void TriggerDeferredConstructionWaitRefresh(VillageStatus status, string source)
    {
        if (_deferredConstructionRefreshRunning || status.Resources.Count == 0)
        {
            return;
        }

        _deferredConstructionRefreshRunning = true;
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
                _deferredConstructionRefreshRunning = false;
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
        var deferredItems = _botService
            .GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => IsConstructionQueueTask(item.TaskName))
            .Where(item => statusVillage is null
                || NormalizeVillageName(GetQueueItemVillageName(item)) is not string itemVillage
                || string.Equals(itemVillage, statusVillage, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var item in deferredItems)
        {
            // Skip items deferred because the build queue was full (not resources). Their timer reflects
            // a build slot freeing up; resuming them just because resources look sufficient causes a brief
            // "Ready" flash before the worker re-defers on the still-full build queue.
            if (item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferReason, out var deferReason)
                && string.Equals(deferReason, BotOptionPayloadKeys.UpgradeDeferReasonQueueFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadDeferredUpgradeRequirements(item.Payload, out var required))
            {
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

                var changed = _botService.UpdateDeferredQueueItem(item.Id, updatedPayload, TimeSpan.Zero);
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
            var updated = _botService.UpdateDeferredQueueItem(item.Id, updatedPayload, delay);
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
            if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.Zero))
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

    private void ResetConstructionBuildQueueTimerForManualRefresh()
    {
        _buildQueueActiveCount = 0;
        _buildQueueRemainingSeconds = 0;
        _buildQueueReachedZeroPendingCompletion = false;
        _continuousLoopConstructionStatusNeedsSync = true;

        UpdateBuildQueueStatusText();
        UpdateAutomationLoopRunningIndicators();
        AppendLog("Construction group re-enabled. Build queue timer reset to Ready; construction will be re-read.");
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

        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var fallbackCooldownSeconds = ResolveTroopTrainingFallbackCooldownSeconds(options.TroopTrainingFallbackCooldownSeconds);
        var deferredItems = _botService
            .GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deferredItems.Count == 0)
        {
            return;
        }

        var requests = BuildDeferredTroopTrainingRequests(options);
        var knownBuildings = _lastBuildingStatus?.Buildings ?? [];
        foreach (var item in deferredItems)
        {
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
