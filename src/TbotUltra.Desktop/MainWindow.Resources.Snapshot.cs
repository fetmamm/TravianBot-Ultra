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
            var selectedVillage = forceCurrentVillage || currentPageOnly ? "(current)" : (GetSelectedVillageName() ?? "-");
            AppendLog($"[resource-refresh] start village='{selectedVillage}'");
            var status = currentPageOnly && IsOfficialTravianServer(effectiveOptions)
                ? await _botService.ReadCurrentPageResourceStatusQuickAsync(effectiveOptions, AppendLog, cancellationToken)
                : await ReadVillageStatusWithRetryAsync(
                    effectiveOptions,
                    cancellationToken,
                    resourceOnly: true,
                    forceCurrentVillage: forceCurrentVillage,
                    currentPageOnly: currentPageOnly);
            AppendLog($"[resource-refresh] read village='{status.ActiveVillage}' | {BuildResourceLogSummary(status)}");

            await Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"[resource-refresh] applied village='{status.ActiveVillage}'");
                ApplyResourceStatusToUi(status);
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] FAIL {ex.Message}");
            throw;
        }
        finally
        {
            AppendLog("[resource-refresh] END");
            _resourceSnapshotRefreshRunning = false;
        }
    }

    private bool ShouldRunBackgroundResourceSnapshotRefresh()
    {
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

        if (!officialServer)
        {
            await TryReviveDeadHeroFromCurrentPageAsync();
            await RefreshInboxIndicatorsQuickAsync();
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
        if (_resourceSnapshotRefreshRunning)
        {
            return;
        }

        SetEnabled(StorageRefreshButton, false);
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
        finally
        {
            SetEnabled(StorageRefreshButton, !_uiBusy);
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
