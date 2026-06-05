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

namespace TbotUltra.Desktop;

public partial class MainWindow
{
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
        _ = Task.Run(async () =>
        {
            try
            {
                await TryRefreshResourceProductionOnlyAsync(CancellationToken.None);
            }
            finally
            {
                _resourceProductionRefreshRunning = false;
                if (_resourceProductionRefreshPending)
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
        status = MergeResourceStatusForUi(status);
        AppendLog($"[resource-ui] village='{status.ActiveVillage}' | {BuildResourceLogSummary(status)}");
        ApplyResourceTransferVillageResourceStatus(status);
        ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
        TriggerDeferredConstructionWaitRefresh(status, "resource_status_refresh");
        TriggerDeferredTroopTrainingWaitRefresh(status, "resource_status_refresh");
    }

    private void ApplyStorageStatusToUi(VillageStatus status, string source)
    {
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
        if (HasCompleteResourceUiSnapshot(status))
        {
            _lastResourceStatusForUi = status;
            return status;
        }

        var previous = _lastResourceStatusForUi;
        if (previous is null)
        {
            return status;
        }

        if (!string.Equals(previous.ActiveVillage, status.ActiveVillage, StringComparison.OrdinalIgnoreCase))
        {
            return status;
        }

        var mergedWarehouse = status.WarehouseCapacity ?? previous.WarehouseCapacity;
        var mergedGranary = status.GranaryCapacity ?? previous.GranaryCapacity;
        var mergedForecasts = BuildMergedResourceForecasts(status, previous, mergedWarehouse, mergedGranary);
        var mergedStatus = status with
        {
            WarehouseCapacity = mergedWarehouse,
            GranaryCapacity = mergedGranary,
            ResourceStorageForecasts = mergedForecasts,
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

    private async Task RefreshResourceSnapshotForUiAsync(
        BotOptions? options = null,
        CancellationToken cancellationToken = default,
        bool forceCurrentVillage = false,
        bool currentPageOnly = false)
    {
        if (_resourceSnapshotRefreshRunning)
        {
            return;
        }

        _resourceSnapshotRefreshRunning = true;
        try
        {
            var effectiveOptions = forceCurrentVillage || currentPageOnly
                ? LoadBotOptions()
                : (options is null ? ApplySelectedVillageToOptions(LoadBotOptions()) : ApplySelectedVillageToOptions(options));
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
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] FAIL {ex.Message}");
            throw;
        }
        finally
        {
            _resourceSnapshotRefreshRunning = false;
        }
    }

    private bool ShouldRunBackgroundResourceSnapshotRefresh()
    {
        // Session sleep is an offline state: the background tick must never read/navigate the browser
        // while sleeping, or it will auto-relogin and defeat the sleep (see ENGINEERING_NOTES §5).
        if (IsSessionSleeping)
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

    private async Task HandleResourceSnapshotRefreshTickAsync()
    {
        if (!ShouldRunBackgroundResourceSnapshotRefresh())
        {
            return;
        }

        var options = LoadBotOptions();
        var officialServer = IsOfficialTravianServer(options);

        try
        {
            await RefreshResourceSnapshotForUiAsync(options, CancellationToken.None, currentPageOnly: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Background resource refresh skipped: {ex.Message}");
        }

        if (officialServer)
        {
            await TryQueueAutoCollectTasksAsync(options);
            await TryQueueAutoCollectDailyQuestsAsync(options);
        }
        else
        {
            await TryReviveDeadHeroFromCurrentPageAsync();
            await RefreshInboxIndicatorsQuickAsync();
        }
    }

    // Official only: cheap current-page check (no navigation) for claimable Questmaster task
    // rewards. When found, queues the collect_tasks runtime task. Gated by the user setting and
    // de-duplicated so the same collection is never queued twice.
    private async Task TryQueueAutoCollectTasksAsync(BotOptions options)
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

        try
        {
            if (await _botService.HasClaimableTasksOnCurrentPageAsync(options, AppendLog, CancellationToken.None))
            {
                _botService.EnqueueRuntime("collect_tasks", "Collect tasks", null, priority: -40, maxRetries: 1);
                AppendLog("Tasks: claimable rewards detected — queued collect_tasks.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Auto collect tasks check skipped: {ex.Message}");
        }
    }

    // Official only: cheap current-page check (no navigation) for claimable Daily Quests rewards.
    // When found, queues the collect_daily_quests runtime task. Gated by the user setting and
    // de-duplicated so the same collection is never queued twice.
    private async Task TryQueueAutoCollectDailyQuestsAsync(BotOptions options)
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

        try
        {
            if (await _botService.HasClaimableDailyQuestsOnCurrentPageAsync(options, AppendLog, CancellationToken.None))
            {
                _botService.EnqueueRuntime("collect_daily_quests", "Collect daily quests", null, priority: -40, maxRetries: 1);
                AppendLog("Daily quests: claimable rewards detected - queued collect_daily_quests.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Auto collect daily quests check skipped: {ex.Message}");
        }
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
            await _botService.CheckAndReviveDeadHeroAsync(options, true, AppendLog, CancellationToken.None);
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
            var status = await _botService.ReadCurrentPageResourceStatusQuickAsync(options, AppendLog, CancellationToken.None);
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
