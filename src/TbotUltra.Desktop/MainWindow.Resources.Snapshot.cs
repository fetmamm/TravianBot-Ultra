using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan CollectTasksVillageCooldown = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTimeOffset> _collectTasksLastQueuedAtByVillage = new(StringComparer.OrdinalIgnoreCase);

    private async Task TryRefreshResourceProductionOnlyAsync(CancellationToken cancellationToken)
    {
        if (_lastResourceStatusForUi is null)
        {
            AppendLog("[resource-production] skipped: no cached resource status for UI.");
            return;
        }

        try
        {
            AppendLog("[resource-production] start");
            var productionByHour = await _botService.ReadCurrentPageResourceProductionPerHourAsync(
                LoadBotOptions(),
                AppendLog,
                cancellationToken);
            if (productionByHour.Count == 0)
            {
                AppendLog("[resource-production] skipped: no production values were read.");
                return;
            }

            var summary = string.Join(", ", new[] { "wood", "clay", "iron", "crop" }
                .Select(key =>
                {
                    productionByHour.TryGetValue(key, out var value);
                    var formatted = value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                    return $"{key}={formatted}/h";
                }));
            AppendLog($"[resource-production] read {summary}");
            await Dispatcher.InvokeAsync(() => ApplyResourceProductionOnlyToUi(productionByHour));
            AppendLog("[resource-production] applied to UI");
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-production] FAIL {ex.Message}");
        }
    }

    private void QueueResourceProductionOnlyRefresh(string source)
    {
        if (_resourceProductionRefreshRunning)
        {
            _resourceProductionRefreshPending = true;
            AppendLog($"[resource-production] pending while previous refresh is running (source={source}).");
            return;
        }

        _resourceProductionRefreshRunning = true;
        _resourceProductionRefreshPending = false;
        AppendLog($"[resource-production] queued from {source}");
        _backgroundTasks.Run(async cancellationToken =>
        {
            try
            {
                await TryRefreshResourceProductionOnlyAsync(cancellationToken);
            }
            finally
            {
                _resourceProductionRefreshRunning = false;
                if (!cancellationToken.IsCancellationRequested && _resourceProductionRefreshPending)
                {
                    _resourceProductionRefreshPending = false;
                    QueueResourceProductionOnlyRefresh("pending_followup");
                }
            }
        });
    }

    private void ApplyResourceProductionOnlyToUi(IReadOnlyDictionary<string, double?> productionByHour)
    {
        if (_lastResourceStatusForUi is null)
        {
            return;
        }

        var currentStatus = _lastResourceStatusForUi;
        var currentForecasts = currentStatus.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);
        var updatedForecasts = new List<ResourceStorageForecast>(4);

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            currentForecasts.TryGetValue(key, out var existingForecast);

            var currentAmount = TryParseResourceValueForUi(currentStatus.Resources, key) ?? existingForecast?.Current;
            var capacity = existingForecast?.Capacity
                ?? (string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                    ? currentStatus.GranaryCapacity
                    : currentStatus.WarehouseCapacity);
            var effectiveProduction = productionByHour.TryGetValue(key, out var liveProduction)
                ? liveProduction
                : existingForecast?.ProductionPerHour;

            double? percentOfCapacity = null;
            if (capacity is > 0 && currentAmount is not null)
            {
                percentOfCapacity = Math.Clamp((double)currentAmount.Value / capacity.Value * 100d, 0d, 100d);
            }

            int? secondsToFull = null;
            if (capacity is > 0 && currentAmount is not null && effectiveProduction is > 0)
            {
                var remaining = Math.Max(0L, capacity.Value - currentAmount.Value);
                var computedSeconds = Math.Ceiling((remaining / effectiveProduction.Value) * 3600d);
                secondsToFull = computedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)computedSeconds;
            }

            updatedForecasts.Add(new ResourceStorageForecast(
                ResourceKey: key,
                Current: currentAmount,
                Capacity: capacity,
                PercentOfCapacity: percentOfCapacity,
                ProductionPerHour: effectiveProduction,
                SecondsToFull: secondsToFull));
        }

        var updatedStatus = currentStatus with
        {
            ResourceStorageForecasts = updatedForecasts,
        };

        _lastResourceStatusForUi = updatedStatus;
        ApplyVillageStatusToUi(updatedStatus);
        TriggerDeferredConstructionWaitRefresh(updatedStatus, "resource_production_refresh");
        TriggerDeferredTroopTrainingWaitRefresh(updatedStatus, "resource_production_refresh");

        var rowCount = _resourcesViewModel.AllFields.Count > 0
            ? _resourcesViewModel.AllFields.Count
            : updatedStatus.ResourceFields.Count;
        UpdateResourcesInfoText(updatedStatus, rowCount);
        AppendLog($"[resource-production] UI summary updated: {BuildResourceForecastSummary(updatedStatus)}");
    }

    private void ApplyResourceStatusToUi(VillageStatus status)
    {
        // The live refresh reads the active (browser) village. Keep the active-village indicator in sync
        // and remember this village's latest read (merged, so buildings from a prior full read persist),
        // but if the user is currently viewing a DIFFERENT village in the dropdown, don't overwrite that
        // village's view with the active village's live data.
        SetActiveWorkingVillageFromStatus(status);
        CacheVillageStatus(status);
        if (!IsStatusForSelectedVillage(status))
        {
            return;
        }

        status = MergeResourceStatusForUi(status);
        var resourceUiVillage = status.ActiveVillage ?? string.Empty;
        // Only re-log when storage capacity or production/h changes (a real event) — not merely because
        // current stock ticked up between reads, which would re-log the echo on every background refresh.
        var resourceUiSignature = BuildResourceUiChangeSignature(status);
        if (!_lastLoggedResourceUiSummaryByVillage.TryGetValue(resourceUiVillage, out var lastResourceUiSignature)
            || lastResourceUiSignature != resourceUiSignature)
        {
            _lastLoggedResourceUiSummaryByVillage[resourceUiVillage] = resourceUiSignature;
            AppendLog($"[resource-ui] village='{status.ActiveVillage}' | {BuildResourceLogSummary(status)}");
        }
        ApplyResourceTransferVillageResourceStatus(status);
        ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
        TriggerDeferredConstructionWaitRefresh(status, "resource_status_refresh");
        TriggerDeferredTroopTrainingWaitRefresh(status, "resource_status_refresh");
    }

    private void ApplyStorageStatusToUi(VillageStatus status, string source)
    {
        SetActiveWorkingVillageFromStatus(status);
        CacheVillageStatus(status);
        if (!IsStatusForSelectedVillage(status))
        {
            AppendLog($"[storage-refresh] skipped UI update from {source}: data is for '{status.ActiveVillage}', another village is selected. Cache updated.");
            return;
        }

        status = MergeResourceStatusForUi(status);
        AppendLog($"[storage-refresh] applied from {source}: {BuildResourceLogSummary(status)}");
        ApplyResourceTransferVillageResourceStatus(status);
        _resourcesViewModel.ApplyStorageForecasts(status);
        TriggerDeferredConstructionWaitRefresh(status, "storage_status_refresh");
        TriggerDeferredTroopTrainingWaitRefresh(status, "storage_status_refresh");
        UpdateResourcesInfoText(status, _resourcesViewModel.AllFields.Count);
    }

    private VillageStatus MergeResourceStatusForUi(VillageStatus status)
    {
        var hasCompleteStorageSnapshot = HasCompleteResourceUiSnapshot(status);
        var hasCompleteFieldSnapshot = HasCompleteResourceFieldSnapshot(status.ResourceFields);
        if (hasCompleteStorageSnapshot && hasCompleteFieldSnapshot)
        {
            _lastResourceStatusForUi = status;
            return status;
        }

        var previous = _lastResourceStatusForUi;
        if (previous is null)
        {
            if (hasCompleteStorageSnapshot)
            {
                _lastResourceStatusForUi = status;
            }

            return status;
        }

        if (!IsSameOrUnknownVillage(previous.ActiveVillage, status.ActiveVillage))
        {
            if (hasCompleteStorageSnapshot)
            {
                _lastResourceStatusForUi = status;
            }

            return status;
        }

        var mergedWarehouse = status.WarehouseCapacity ?? previous.WarehouseCapacity;
        var mergedGranary = status.GranaryCapacity ?? previous.GranaryCapacity;
        var mergedForecasts = BuildMergedResourceForecasts(status, previous, mergedWarehouse, mergedGranary);
        var mergedResourceFields = hasCompleteFieldSnapshot || !HasCompleteResourceFieldSnapshot(previous.ResourceFields)
            ? status.ResourceFields
            : previous.ResourceFields;
        var mergedStatus = status with
        {
            WarehouseCapacity = mergedWarehouse,
            GranaryCapacity = mergedGranary,
            ResourceStorageForecasts = mergedForecasts,
            ResourceFields = mergedResourceFields,
        };

        _lastResourceStatusForUi = mergedStatus;
        AppendLog($"[resource-ui] preserved previous storage/prod data for village='{status.ActiveVillage}'.");
        return mergedStatus;
    }

    private static bool HasCompleteResourceUiSnapshot(VillageStatus status)
    {
        if (status.WarehouseCapacity is null || status.GranaryCapacity is null)
        {
            return false;
        }

        if (status.ResourceStorageForecasts is null || status.ResourceStorageForecasts.Count == 0)
        {
            return false;
        }

        return status.ResourceStorageForecasts.Any(item => item.Capacity is not null || item.ProductionPerHour is not null);
    }

    private static bool HasCompleteResourceFieldSnapshot(IReadOnlyList<ResourceField>? fields)
    {
        if (fields is null)
        {
            return false;
        }

        var bySlot = fields
            .Where(field => field.SlotId is >= 1 and <= 18)
            .GroupBy(field => field.SlotId!.Value)
            .ToList();
        if (bySlot.Count != 18)
        {
            return false;
        }

        return bySlot.All(group =>
        {
            var field = group.First();
            return field.Level is >= 0
                && (BuildingCatalogService.GidForName(field.Name) is not null
                    || BuildingCatalogService.GidForName(field.FieldType) is not null);
        });
    }

    private static bool IsSameOrUnknownVillage(string? left, string? right)
    {
        var normalizedLeft = NormalizeVillageName(left);
        var normalizedRight = NormalizeVillageName(right);
        return normalizedLeft is null
            || normalizedRight is null
            || string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ResourceStorageForecast> BuildMergedResourceForecasts(
        VillageStatus current,
        VillageStatus previous,
        long? warehouseCapacity,
        long? granaryCapacity)
    {
        var currentForecasts = current.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);
        var previousForecasts = previous.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        var result = new List<ResourceStorageForecast>(4);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            currentForecasts.TryGetValue(key, out var currentForecast);
            previousForecasts.TryGetValue(key, out var previousForecast);

            var currentAmount = TryParseResourceValueForUi(current.Resources, key)
                ?? currentForecast?.Current
                ?? previousForecast?.Current;
            var capacity = currentForecast?.Capacity
                ?? previousForecast?.Capacity
                ?? (string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase) ? granaryCapacity : warehouseCapacity);
            var productionPerHour = currentForecast?.ProductionPerHour ?? previousForecast?.ProductionPerHour;

            double? percentOfCapacity = null;
            if (capacity is > 0 && currentAmount is not null)
            {
                percentOfCapacity = Math.Clamp((double)currentAmount.Value / capacity.Value * 100d, 0d, 100d);
            }

            int? secondsToFull = null;
            if (capacity is > 0 && currentAmount is not null && productionPerHour is > 0)
            {
                var remaining = Math.Max(0L, capacity.Value - currentAmount.Value);
                var computedSeconds = Math.Ceiling((remaining / productionPerHour.Value) * 3600d);
                secondsToFull = computedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)computedSeconds;
            }

            result.Add(new ResourceStorageForecast(
                ResourceKey: key,
                Current: currentAmount,
                Capacity: capacity,
                PercentOfCapacity: percentOfCapacity,
                ProductionPerHour: productionPerHour,
                SecondsToFull: secondsToFull));
        }

        return result;
    }

    private static long? TryParseResourceValueForUi(IReadOnlyDictionary<string, string>? resources, string key)
    {
        if (resources is null || !resources.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace(" ", string.Empty).Replace("'", string.Empty).Replace(",", string.Empty).Trim();
        return long.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private async Task<VillageStatus?> RefreshResourceSnapshotForUiAsync(
        BotOptions? options = null,
        CancellationToken cancellationToken = default,
        bool forceCurrentVillage = false,
        bool currentPageOnly = false)
    {
        if (_resourceSnapshotRefreshRunning)
        {
            return null;
        }

        _resourceSnapshotRefreshRunning = true;
        try
        {
            var effectiveOptions = forceCurrentVillage || currentPageOnly
                ? LoadBotOptions()
                : (options is null ? ApplySelectedVillageToOptions(LoadBotOptions()) : ApplySelectedVillageToOptions(options));
            if (IsOfficialTravianServer(effectiveOptions))
            {
                await EnsureTravianLanguageForCurrentPageAsync(effectiveOptions, cancellationToken);
            }

            var status = currentPageOnly && IsOfficialTravianServer(effectiveOptions)
                ? await _botService.ReadCurrentPageResourceStatusQuickAsync(effectiveOptions, AppendLog, cancellationToken)
                : await ReadVillageStatusWithRetryAsync(
                    effectiveOptions,
                    cancellationToken,
                    resourceOnly: true,
                    forceCurrentVillage: forceCurrentVillage,
                    currentPageOnly: currentPageOnly);

            await Dispatcher.InvokeAsync(() =>
            {
                ApplyResourceStatusToUi(status);
                // Pick up renamed/new villages read from the current page (guarded so it never blanks
                // the list when the page had no readable village info).
                TryUpdateDashboardVillagesFromStatus(status);
                // Keep the hero/dashboard adventure count in sync each refresh when the indicator was
                // readable on the current page (null = not found, so leave the last value untouched).
                if (status.AdventureCount is int adventureCount)
                {
                    ApplyHeroAdventureAvailability(adventureCount);
                }
            });
            return status;
        }
        catch (UnexpectedTravianLanguageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A page caught mid-navigation reports login state 'unknown' and self-heals on the next
            // read. It is never user-actionable, so log it as a verbose (non-alarm) line instead of a
            // red FAIL alarm. Real session and navigation failures still keep alarming.
            if (IsTransientPageReadFailure(ex))
            {
                AppendLog($"[resource-refresh:verbose] transient page read skipped ({ex.Message})");
            }
            else
            {
                AppendLog($"[resource-refresh] FAIL {ex.Message}");
            }

            throw;
        }
        finally
        {
            _resourceSnapshotRefreshRunning = false;
        }
    }

    private async Task EnsureTravianLanguageForCurrentPageAsync(BotOptions options, CancellationToken cancellationToken)
    {
        if (!options.AutomaticallyCheckLanguage)
        {
            return;
        }

        var language = await _botService.ReadCurrentLanguageAsync(options, AppendLog, cancellationToken);
        if (string.IsNullOrWhiteSpace(language))
        {
            AppendLog("[language:verbose] current-page language unknown during background refresh; skipping this tick.");
            return;
        }

        if (!string.Equals(language?.Trim(), TravianClient.ExpectedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnexpectedTravianLanguageException(language);
        }
    }

    // Benign read failures that self-heal on the next operation. Captcha, manual-step and logged-out
    // remain alarms; a crashed target is discarded by BotTaskRunner before this exception returns.
    private static bool IsTransientPageReadFailure(Exception ex)
    {
        return IsTransientConnectionFailure(ex)
            || BrowserFailureClassifier.IsTargetCrash(ex);
    }

    internal static bool IsTransientConnectionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TransientNavigationException
                || current.Message.Contains("page state is 'unknown'", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private DateTimeOffset _transientNetworkUnavailableUntilUtc;
    private int _consecutiveTransientConnectionFailures;

    private void MarkTransientNetworkUnavailable(TimeSpan delay)
    {
        var unavailableUntil = DateTimeOffset.UtcNow + delay;
        if (unavailableUntil > _transientNetworkUnavailableUntilUtc)
        {
            _transientNetworkUnavailableUntilUtc = unavailableUntil;
        }
    }

    private bool IsTransientNetworkUnavailable()
        => DateTimeOffset.UtcNow < _transientNetworkUnavailableUntilUtc;

    private TimeSpan GetTransientNetworkUnavailableRemaining()
    {
        var remaining = _transientNetworkUnavailableUntilUtc - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan NextTransientNavigationRetryDelay()
    {
        _consecutiveTransientConnectionFailures = Math.Min(_consecutiveTransientConnectionFailures + 1, 3);
        var minimumSeconds = 30 * (1 << (_consecutiveTransientConnectionFailures - 1));
        return TimeSpan.FromSeconds(Random.Shared.Next(minimumSeconds, minimumSeconds * 2 + 1));
    }

    private void MarkNetworkConnectionHealthy()
    {
        _consecutiveTransientConnectionFailures = 0;
        _transientNetworkUnavailableUntilUtc = DateTimeOffset.MinValue;
        ResetAutomaticProxyRecoveryRetry();
    }

    private bool ShouldRunBackgroundResourceSnapshotRefresh()
    {
        // Session sleep is an offline state: the background tick must never read/navigate the browser
        // while sleeping, or it will auto-relogin and defeat the sleep (see ENGINEERING_NOTES §5).
        if (IsSessionSleeping)
        {
            return false;
        }

        if (IsTransientNetworkUnavailable())
        {
            return false;
        }

        // During an account switch the tick must never take the session gate: right after logout it
        // would silently log the OLD account back in before the new account is applied.
        if (_accountSwitchInProgress)
        {
            return false;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen || _resourceSnapshotRefreshRunning)
        {
            return false;
        }

        return !_uiBusy;
    }

    private bool _heroReviveCheckRunning;

    // Last "[resource-ui]" summary logged per village, so that echo prints once and then only again
    // when storage/production actually changes instead of on every background refresh.
    private readonly System.Collections.Generic.Dictionary<string, string> _lastLoggedResourceUiSummaryByVillage = new();

    // Background resource-snapshot refresher (fires on the jittered dashboard timer).
    private async Task HandleResourceSnapshotRefreshTickAsync()
    {
        if (!ShouldRunBackgroundResourceSnapshotRefresh())
        {
            return;
        }

        var options = LoadBotOptions();
        var officialServer = IsOfficialTravianServer(options);

        VillageStatus? refreshedStatus = null;
        try
        {
            // Session-scoped token: stop/account switch aborts an in-flight background refresh
            // instead of letting it sit on the worker session gate.
            refreshedStatus = await RefreshResourceSnapshotForUiAsync(options, _loopController.AcquireSessionScopeToken(), currentPageOnly: true);
        }
        catch (Exception ex)
        {
            if (ex is UnexpectedTravianLanguageException languageException)
            {
                await HandleUnexpectedTravianLanguageAsync(languageException);
                return;
            }

            if (IsTransientPageReadFailure(ex))
            {
                MarkTransientNetworkUnavailable(TimeSpan.FromSeconds(30));
                AppendLog($"[resource-refresh:verbose] background refresh skipped after transient page failure ({ex.Message})");
            }
            else
            {
                AppendLog($"Background resource refresh skipped: {ex.Message}");
            }
        }

        await TryReleaseRevivingHeroManageDeferAsync(options);

        await TryQueueSpendHeroAttributePointsForLevelUpIndicatorAsync(options);

        if (officialServer && refreshedStatus is not null)
        {
            await TryQueueAutoCollectTasksAsync(options, refreshedStatus);
            await TryQueueAutoCollectDailyQuestsAsync(options, refreshedStatus);
            TryQueueReadDailyResetHour(options);
            TryQueueActivateProductionBonus(options);
        }
        else
        {
            await TryReviveDeadHeroFromCurrentPageAsync();
            await RefreshInboxIndicatorsQuickAsync();
        }
    }

    private async Task TryQueueSpendHeroAttributePointsForLevelUpIndicatorAsync(BotOptions options)
    {
        if (!IsHeroAutoAssignPointsEnabledNow(options) || HasActiveSpendHeroAttributePointsTask())
        {
            return;
        }

        if (!IsQueueItemAllowedByAutomationSettings(new QueueItem
            {
                TaskName = "spend_hero_attribute_points",
                Group = QueueGroup.Hero,
            }))
        {
            return;
        }

        bool hasLevelUpIndicator;
        try
        {
            hasLevelUpIndicator = await _botService.HasHeroLevelUpIndicatorOnCurrentPageAsync(
                options,
                AppendLog,
                _loopController.AcquireSessionScopeToken());
        }
        catch (Exception ex)
        {
            if (IsTransientPageReadFailure(ex))
            {
                AppendLog($"[hero:verbose] level-up indicator check skipped after transient page failure ({ex.Message})");
            }
            else
            {
                AppendLog($"Hero level-up indicator check skipped: {ex.Message}");
            }

            return;
        }

        if (!hasLevelUpIndicator || HasActiveSpendHeroAttributePointsTask())
        {
            return;
        }

        var payload = BuildHeroRuntimePayload();
        _botService.EnqueueRuntime("spend_hero_attribute_points", "Hero attribute points", payload, priority: -50, maxRetries: 0);
        AppendLog($"Hero attributes: queued spend_hero_attribute_points because levelUp indicator is visible. priority={payload[BotOptionPayloadKeys.HeroStatPriority]}");
        WakeContinuousLoopForHeroAttributePoints();
    }

    private void WakeContinuousLoopForHeroAttributePoints()
    {
        if (!IsContinuousLoopRunning())
        {
            TriggerQueueAutoRunFromEnqueue();
            return;
        }

        Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        AppendLog("Hero attributes: continuous loop wake requested for spend_hero_attribute_points.");
    }

    private bool IsHeroAutoAssignPointsEnabledNow(BotOptions options)
    {
        if (!options.HeroAutoAssignPoints)
        {
            return false;
        }

        if (Dispatcher.CheckAccess())
        {
            return _heroViewModel.AutoAssignPoints;
        }

        return Dispatcher.Invoke(() => _heroViewModel.AutoAssignPoints);
    }

    private bool HasActiveSpendHeroAttributePointsTask()
    {
        return _botService.GetQueueItemsForDisplay()
            .Any(item =>
                string.Equals(item.TaskName, "spend_hero_attribute_points", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
    }

    // Official only: cheap current-page check (no navigation) for claimable Questmaster task
    // rewards. When found, queues the collect_tasks runtime task. Gated by the user setting, village
    // automation settings, and de-duplicated so the same collection is never queued twice.
    private async Task TryQueueAutoCollectTasksAsync(BotOptions options, VillageStatus? observedStatus = null)
    {
        if (!IsAutoCollectTasksEnabledNow(options))
        {
            // Setting turned off — make sure nothing that was queued earlier keeps running.
            RemovePendingCollectTasks();
            return;
        }

        if (HasActiveCollectTasksTask())
        {
            return;
        }

        var payload = BuildCurrentVillageUtilityPayload(observedStatus);
        if (payload is null)
        {
            return;
        }

        if (!IsUtilityTaskAllowedByAutomationSettings("collect_tasks", payload))
        {
            return;
        }

        var villageCooldownKey = GetUtilityTaskVillageKey(payload);
        var now = DateTimeOffset.UtcNow;
        if (villageCooldownKey is not null
            && _collectTasksLastQueuedAtByVillage.TryGetValue(villageCooldownKey, out var lastQueuedAt)
            && now - lastQueuedAt < CollectTasksVillageCooldown)
        {
            return;
        }

        try
        {
            if (await _botService.HasClaimableTasksOnCurrentPageAsync(options, AppendLog, _loopController.AcquireSessionScopeToken()))
            {
                _botService.EnqueueRuntime("collect_tasks", "Collect tasks", payload, priority: -40, maxRetries: 1);
                if (villageCooldownKey is not null)
                {
                    _collectTasksLastQueuedAtByVillage[villageCooldownKey] = now;
                }
                AppendLog("Tasks: claimable rewards detected — queued collect_tasks.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Auto collect tasks check skipped: {ex.Message}");
        }
    }

    // Official only: cheap current-page check (no navigation) for claimable Daily Quests rewards.
    // When found, queues the collect_daily_quests runtime task. Gated by the user setting, village
    // automation settings, and de-duplicated so the same collection is never queued twice.
    private async Task TryQueueAutoCollectDailyQuestsAsync(BotOptions options, VillageStatus? observedStatus = null)
    {
        if (!IsAutoCollectDailyQuestsEnabledNow(options))
        {
            RemovePendingCollectDailyQuests();
            return;
        }

        if (HasActiveCollectDailyQuestsTask())
        {
            return;
        }

        var payload = BuildCurrentVillageUtilityPayload(observedStatus);
        if (payload is null)
        {
            return;
        }

        if (!IsUtilityTaskAllowedByAutomationSettings("collect_daily_quests", payload))
        {
            return;
        }

        try
        {
            if (await _botService.HasClaimableDailyQuestsOnCurrentPageAsync(options, AppendLog, _loopController.AcquireSessionScopeToken()))
            {
                _botService.EnqueueRuntime("collect_daily_quests", "Collect daily quests", payload, priority: -40, maxRetries: 1);
                AppendLog("Daily quests: claimable rewards detected - queued collect_daily_quests.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Auto collect daily quests check skipped: {ex.Message}");
        }
    }

    private Dictionary<string, string>? BuildCurrentVillageUtilityPayload(VillageStatus? observedStatus)
    {
        var villageName = NormalizeVillageName(observedStatus?.ActiveVillage)
            ?? NormalizeVillageName(_activeWorkingVillageName);
        if (villageName is null)
        {
            return null;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetVillageName] = villageName,
        };

        var villageUrl = observedStatus?.Villages
            .FirstOrDefault(item => string.Equals(
                NormalizeVillageName(item.Name),
                villageName,
                StringComparison.OrdinalIgnoreCase))
            ?.Url;
        if (string.IsNullOrWhiteSpace(villageUrl))
        {
            villageUrl = GetEnabledAutomationVillages()
                .FirstOrDefault(item => string.Equals(
                    NormalizeVillageName(item.Name),
                    villageName,
                    StringComparison.OrdinalIgnoreCase))
                ?.Url;
        }

        if (!string.IsNullOrWhiteSpace(villageUrl))
        {
            payload[BotOptionPayloadKeys.TargetVillageUrl] = villageUrl;
        }

        return payload;
    }

    private bool IsUtilityTaskAllowedByAutomationSettings(string taskName, Dictionary<string, string> payload)
    {
        return IsQueueItemAllowedByAutomationSettings(new QueueItem
        {
            TaskName = taskName,
            Group = QueueGroupCatalog.ResolveGroup(taskName),
            Payload = payload,
        });
    }

    private static string? GetUtilityTaskVillageKey(IReadOnlyDictionary<string, string>? payload)
    {
        if (payload is null)
        {
            return null;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.TargetVillageUrl, out var villageUrl)
            && !string.IsNullOrWhiteSpace(villageUrl))
        {
            return $"url:{villageUrl.Trim()}";
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.TargetVillageName, out var villageName)
            && NormalizeVillageName(villageName) is string normalizedName)
        {
            return $"name:{normalizedName}";
        }

        return null;
    }

    private bool IsAutoCollectTasksEnabledNow(BotOptions options)
    {
        if (!options.AutoCollectTasksEnabled)
        {
            return false;
        }

        return ReadCheckBoxChecked(AutoCollectTasksCheckBox, fallback: options.AutoCollectTasksEnabled);
    }

    private bool IsAutoCollectDailyQuestsEnabledNow(BotOptions options)
    {
        if (!options.AutoCollectDailyQuestsEnabled)
        {
            return false;
        }

        return ReadCheckBoxChecked(AutoCollectDailyQuestsCheckBox, fallback: options.AutoCollectDailyQuestsEnabled);
    }

    private bool IsAutoCollectUtilityTaskEnabledNow(string? taskName, BotOptions options)
    {
        if (string.Equals(taskName, "collect_tasks", StringComparison.OrdinalIgnoreCase))
        {
            return IsAutoCollectTasksEnabledNow(options);
        }

        if (string.Equals(taskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase))
        {
            return IsAutoCollectDailyQuestsEnabledNow(options);
        }

        return true;
    }

    private void RemoveDisabledAutoCollectUtilityItems(BotOptions options)
    {
        if (!IsAutoCollectTasksEnabledNow(options))
        {
            RemovePendingCollectTasks();
        }

        if (!IsAutoCollectDailyQuestsEnabledNow(options))
        {
            RemovePendingCollectDailyQuests();
        }
    }

    private bool ReadCheckBoxChecked(System.Windows.Controls.CheckBox? checkBox, bool fallback)
    {
        if (checkBox is null)
        {
            return fallback;
        }

        if (Dispatcher.CheckAccess())
        {
            return checkBox.IsChecked == true;
        }

        return Dispatcher.Invoke(() => checkBox.IsChecked == true);
    }

    private bool HasActiveCollectDailyQuestsTask()
    {
        return _botService.GetQueueItemsForDisplay()
            .Any(item =>
                string.Equals(item.TaskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
    }

    private void RemovePendingCollectDailyQuests()
    {
        var pending = _botService.GetQueueItemsForDisplay()
            .Where(item =>
                string.Equals(item.TaskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Paused)
            .ToList();

        foreach (var item in pending)
        {
            _botService.RemoveQueueItem(item.Id);
        }

        if (pending.Count > 0)
        {
            AppendLog($"Daily quests: auto-collect disabled - removed {pending.Count} queued collect_daily_quests item(s).");
        }
    }

    private bool HasActiveCollectTasksTask()
    {
        return _botService.GetQueueItemsForDisplay()
            .Any(item =>
                string.Equals(item.TaskName, "collect_tasks", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
    }

    // Drops queued (not yet running) collect_tasks items — used when the setting is turned off so a
    // previously-detected collection can't run after the user disabled the feature.
    private void RemovePendingCollectTasks()
    {
        var pending = _botService.GetQueueItemsForDisplay()
            .Where(item =>
                string.Equals(item.TaskName, "collect_tasks", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Paused)
            .ToList();

        foreach (var item in pending)
        {
            _botService.RemoveQueueItem(item.Id);
        }

        if (pending.Count > 0)
        {
            AppendLog($"Tasks: auto-collect disabled — removed {pending.Count} queued collect_tasks item(s).");
        }
    }

    private static bool IsOfficialTravianServer(BotOptions options)
    {
        return Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("travian.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".travian.com", StringComparison.OrdinalIgnoreCase));
    }

    // Payload marker the failure handler writes onto a hero_manage that deferred for the full revive
    // countdown (see HandleQueueItemFailureAsync), so this refresh can recognise and release it.
    private const string HeroDeferReasonKey = "hero_defer_reason";
    private const string HeroDeferReasonReviving = "reviving";

    // Releases a hero_manage that was deferred for the full revive time when the hero is no longer reviving
    // on the current page (e.g. the user revived early with a bucket), so the loop re-runs it now and
    // resumes adventures instead of idling out the original countdown. Cheap: only reads the page when such
    // a deferred item actually exists.
    private async Task TryReleaseRevivingHeroManageDeferAsync(BotOptions options)
    {
        if (_heroReviveCheckRunning)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var deferredReviving = _botService.GetQueueItemsForDisplay()
            .Where(item => string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => item.NextAttemptAt > now.AddSeconds(5))
            .Where(item => item.Payload.TryGetValue(HeroDeferReasonKey, out var reason)
                && string.Equals(reason, HeroDeferReasonReviving, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deferredReviving.Count == 0)
        {
            return;
        }

        bool stillReviving;
        _heroReviveCheckRunning = true;
        try
        {
            stillReviving = await _botService.IsHeroRevivingOnCurrentPageAsync(options, AppendLog, _loopController.AcquireSessionScopeToken());
        }
        catch (Exception ex)
        {
            if (IsTransientPageReadFailure(ex))
            {
                AppendLog($"[hero:verbose] reviving-release check skipped after transient page failure ({ex.Message})");
            }
            else
            {
                AppendLog($"Hero reviving-release check skipped: {ex.Message}");
            }

            return;
        }
        finally
        {
            _heroReviveCheckRunning = false;
        }

        if (stillReviving)
        {
            return;
        }

        var released = 0;
        foreach (var item in deferredReviving)
        {
            var clearedPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
            clearedPayload.Remove(HeroDeferReasonKey);
            if (_botService.UpdateDeferredQueueItem(item.Id, clearedPayload, TimeSpan.Zero))
            {
                released++;
            }
        }

        if (released > 0)
        {
            AppendLog($"Hero: revive finished early (no reviving icon) — released {released} deferred hero_manage item(s) to run now.");
            if (IsContinuousLoopRunning())
            {
                Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            }
            else
            {
                TriggerQueueAutoRunFromEnqueue();
            }
        }
    }

    private async Task TryReviveDeadHeroFromCurrentPageAsync()
    {
        if (_heroReviveCheckRunning)
        {
            return;
        }

        var options = LoadBotOptions();
        if (!options.HeroAutoRevive)
        {
            return;
        }

        _heroReviveCheckRunning = true;
        try
        {
            await _botService.CheckAndReviveDeadHeroAsync(options, true, AppendLog, _loopController.AcquireSessionScopeToken());
        }
        catch (Exception ex)
        {
            AppendLog($"Hero revive check skipped: {ex.Message}");
        }
        finally
        {
            _heroReviveCheckRunning = false;
        }
    }

    private async void StorageRefreshButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(StorageRefreshButtonClickAsync);

    private async Task StorageRefreshButtonClickAsync()
    {
        if (BlockIfSessionSleeping("Storage refresh"))
        {
            return;
        }

        if (_resourceSnapshotRefreshRunning)
        {
            return;
        }

        try
        {
            await EnsureChromiumInstalledAsync();
            AppendLog("[resource-refresh] manual quick refresh requested");
            var options = LoadBotOptions();
            var status = await _botService.ReadCurrentPageResourceStatusQuickAsync(
                options,
                AppendLog,
                _loopController.AcquireSessionScopeToken());
            ApplyResourceStatusToUi(status);
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] manual quick refresh skipped: {ex.Message}");
        }
    }

    private int ResolveResourceMaxLevelFromStatus(VillageStatus status)
    {
        if (status.IsCapital == true)
        {
            return ResourceFieldMaxLevel;
        }

        if (status.IsCapital == false)
        {
            return NonCapitalResourceMaxLevel;
        }

        return _activeVillageResourceMaxLevel;
    }

    private void UpdateActiveVillageResourceMaxLevel(VillageStatus status)
    {
        if (status.IsCapital == true)
        {
            _activeVillageResourceMaxLevel = ResourceFieldMaxLevel;
            return;
        }

        if (status.IsCapital == false)
        {
            _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
        }
    }

    private static string BuildResourceForecastSummary(VillageStatus status)
    {
        if (status.ResourceStorageForecasts is null || status.ResourceStorageForecasts.Count == 0)
        {
            return "Storage forecast unavailable.";
        }

        var parts = new List<string>();
        foreach (var forecast in status.ResourceStorageForecasts)
        {
            var key = forecast.ResourceKey switch
            {
                "wood" => "Wood",
                "clay" => "Clay",
                "iron" => "Iron",
                "crop" => "Crop",
                _ => forecast.ResourceKey,
            };
            var percentText = forecast.PercentOfCapacity is double percent
                ? $"{percent:F0}%"
                : "-";
            var etaText = forecast.SecondsToFull is int seconds
                ? FormatCountdown(seconds)
                : "-";
            parts.Add($"{key} {percentText} (full in {etaText})");
        }

        var warehouse = FormatResourceLogNumber(status.WarehouseCapacity);
        var granary = FormatResourceLogNumber(status.GranaryCapacity);
        return $"Warehouse={warehouse}, Granary={granary}. {string.Join(" | ", parts)}";
    }

    // Signature of the "meaningful" resource-UI fields (storage capacity + production/h) used to decide
    // whether to re-log the [resource-ui] echo. Excludes current stock, which changes on every read.
    private static string BuildResourceUiChangeSignature(VillageStatus status)
    {
        var forecasts = status.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        string Prod(string key)
        {
            forecasts.TryGetValue(key, out var forecast);
            return FormatResourceLogNumber(forecast?.ProductionPerHour);
        }

        return $"{FormatResourceLogNumber(status.WarehouseCapacity)}/{FormatResourceLogNumber(status.GranaryCapacity)}"
            + $"|{Prod("wood")}|{Prod("clay")}|{Prod("iron")}|{Prod("crop")}";
    }

    private static string BuildResourceLogSummary(VillageStatus status)
    {
        var forecasts = status.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        string Part(string key, string label)
        {
            forecasts.TryGetValue(key, out var forecast);
            var current = FormatResourceLogNumber(forecast?.Current);
            var production = FormatResourceLogNumber(forecast?.ProductionPerHour);
            return $"{label} {current} @{production}/h";
        }

        return $"storage {FormatResourceLogNumber(status.WarehouseCapacity)}/{FormatResourceLogNumber(status.GranaryCapacity)} | {Part("wood", "W")} | {Part("clay", "C")} | {Part("iron", "I")} | {Part("crop", "Crop")}";
    }

    private static string FormatResourceLogNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
    }

    private static string FormatResourceLogNumber(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "-";
        }

        return Math.Round(value.Value, MidpointRounding.AwayFromZero)
            .ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", " ");
    }
}
