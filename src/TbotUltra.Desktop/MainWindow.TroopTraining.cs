using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void InitializeTroopTrainingBuildingOptions()
    {
        if (_troopTrainingBuildingOptions.Count > 0)
        {
            return;
        }

        foreach (var option in new[]
                 {
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Barracks, Title = "Barracks" },
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Stable, Title = "Stable" },
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Workshop, Title = "Workshop" },
                 })
        {
            option.PropertyChanged += TroopTrainingBuildingOption_PropertyChanged;
            _troopTrainingBuildingOptions.Add(option);
        }

        UpdateTroopTrainingTroopOptions(ResolveStoredTroopTrainingTribe());
        ResetTroopTrainingQueueStatus();
    }

    private void TroopTrainingBuildingOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressTroopTrainingConfigWrite)
        {
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.IsEnabled), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.SelectedTroop), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.MaxQueueMode), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.AmountMode), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.KeepResourcesPercent), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.RunMode), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.MinimumTroops), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.MinimumResourcesPercent), StringComparison.Ordinal))
        {
            return;
        }

        PersistTroopTrainingConfig();
        UpdateAutomationLoopRunningIndicators();
    }

    private string ResolveStoredTroopTrainingTribe()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && !string.IsNullOrWhiteSpace(analysis.Tribe))
            {
                return analysis.Tribe;
            }
        }
        catch
        {
            // Ignore temporary analysis read failures.
        }

        return TribeInfoTextBlock?.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim() ?? "Unknown";
    }

    private void LoadTroopTrainingConfigToUi(BotOptions options)
    {
        _suppressTroopTrainingConfigWrite = true;
        try
        {
            foreach (var option in _troopTrainingBuildingOptions)
            {
                switch (option.BuildingType)
                {
                    case TroopTrainingBuildingType.Barracks:
                        option.IsEnabled = options.TroopTrainingBarracksEnabled;
                        option.SelectedTroop = options.TroopTrainingBarracksTroopType;
                        option.MaxQueueMode = options.TroopTrainingBarracksMaxQueueHours;
                        option.AmountMode = options.TroopTrainingBarracksAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingBarracksKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingBarracksRunMode;
                        option.MinimumTroops = options.TroopTrainingBarracksMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingBarracksMinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Stable:
                        option.IsEnabled = options.TroopTrainingStableEnabled;
                        option.SelectedTroop = options.TroopTrainingStableTroopType;
                        option.MaxQueueMode = options.TroopTrainingStableMaxQueueHours;
                        option.AmountMode = options.TroopTrainingStableAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingStableKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingStableRunMode;
                        option.MinimumTroops = options.TroopTrainingStableMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingStableMinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Workshop:
                        option.IsEnabled = options.TroopTrainingWorkshopEnabled;
                        option.SelectedTroop = options.TroopTrainingWorkshopTroopType;
                        option.MaxQueueMode = options.TroopTrainingWorkshopMaxQueueHours;
                        option.AmountMode = options.TroopTrainingWorkshopAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingWorkshopKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingWorkshopRunMode;
                        option.MinimumTroops = options.TroopTrainingWorkshopMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingWorkshopMinimumResourcesPercent;
                        break;
                }
            }
        }
        finally
        {
            _suppressTroopTrainingConfigWrite = false;
        }
    }

    private void PersistTroopTrainingConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            foreach (var option in _troopTrainingBuildingOptions)
            {
                switch (option.BuildingType)
                {
                    case TroopTrainingBuildingType.Barracks:
                        config[BotOptionPayloadKeys.TroopTrainingBarracksEnabled] = option.IsEnabled;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksTroopType] = option.SelectedTroop;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] = option.MaxQueueMode;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] = option.AmountMode;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent] = option.KeepResourcesPercent;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksRunMode] = option.RunMode;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops] = option.MinimumTroops;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent] = option.MinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Stable:
                        config[BotOptionPayloadKeys.TroopTrainingStableEnabled] = option.IsEnabled;
                        config[BotOptionPayloadKeys.TroopTrainingStableTroopType] = option.SelectedTroop;
                        config[BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours] = option.MaxQueueMode;
                        config[BotOptionPayloadKeys.TroopTrainingStableAmountMode] = option.AmountMode;
                        config[BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent] = option.KeepResourcesPercent;
                        config[BotOptionPayloadKeys.TroopTrainingStableRunMode] = option.RunMode;
                        config[BotOptionPayloadKeys.TroopTrainingStableMinimumTroops] = option.MinimumTroops;
                        config[BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent] = option.MinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Workshop:
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopEnabled] = option.IsEnabled;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopTroopType] = option.SelectedTroop;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours] = option.MaxQueueMode;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode] = option.AmountMode;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent] = option.KeepResourcesPercent;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopRunMode] = option.RunMode;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops] = option.MinimumTroops;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent] = option.MinimumResourcesPercent;
                        break;
                }
            }

            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save troop training config: {ex.Message}");
        }
    }

    private bool UpdateTroopTrainingTroopOptions(string? tribe)
    {
        var configChanged = false;
        _suppressTroopTrainingConfigWrite = true;
        try
        {
            foreach (var option in _troopTrainingBuildingOptions)
            {
                var resolvedTroops = TroopCatalog.ResolveTroopTypesForTribe(tribe, option.BuildingType);
                var currentSelection = option.SelectedTroop;
                option.TroopOptions.Clear();
                foreach (var troop in resolvedTroops)
                {
                    option.TroopOptions.Add(troop);
                }

                if (resolvedTroops.Contains(currentSelection, StringComparer.OrdinalIgnoreCase))
                {
                    option.SelectedTroop = resolvedTroops.First(item => string.Equals(item, currentSelection, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var fallbackTroop = resolvedTroops.FirstOrDefault() ?? string.Empty;
                    if (!string.Equals(option.SelectedTroop, fallbackTroop, StringComparison.Ordinal))
                    {
                        configChanged = true;
                    }

                    option.SelectedTroop = fallbackTroop;
                }
            }
        }
        finally
        {
            _suppressTroopTrainingConfigWrite = false;
        }

        return configChanged;
    }

    private void ApplyTroopTrainingStatusToUi(VillageStatus status)
    {
        var queueStatuses = status.TroopTrainingQueues ?? _lastBuildingStatus?.TroopTrainingQueues;
        foreach (var option in _troopTrainingBuildingOptions)
        {
            var queueStatus = queueStatuses?.FirstOrDefault(item => item.BuildingType == option.BuildingType);
            if (queueStatus is not null)
            {
                option.Exists = queueStatus.Exists;
                option.QueueRemainingSeconds = queueStatus.RemainingSeconds;
                option.QueueStatusText = queueStatus.Exists
                    ? $"Queue: {queueStatus.RemainingText}"
                    : "Building not found";
                continue;
            }

            if (option.Exists)
            {
                if (string.IsNullOrWhiteSpace(option.QueueStatusText))
                {
                    option.QueueStatusText = "Queue not loaded.";
                }

                continue;
            }

            var buildingExists = status.Buildings.Any(item =>
                item.SlotId is > 0
                && ((option.BuildingType == TroopTrainingBuildingType.Barracks && (item.Gid ?? 0) == 19)
                    || (option.BuildingType == TroopTrainingBuildingType.Stable && (item.Gid ?? 0) == 20)
                    || (option.BuildingType == TroopTrainingBuildingType.Workshop && (item.Gid ?? 0) == 21)
                    || string.Equals(item.Name, option.Title, StringComparison.OrdinalIgnoreCase)));
            option.Exists = buildingExists;
            if (!buildingExists)
            {
                option.QueueRemainingSeconds = null;
                option.QueueStatusText = "Building not found";
            }
            else if (string.IsNullOrWhiteSpace(option.QueueStatusText))
            {
                option.QueueStatusText = "Queue not loaded.";
            }
        }
    }

    private void ResetTroopTrainingQueueStatus()
    {
        foreach (var option in _troopTrainingBuildingOptions)
        {
            option.Exists = false;
            option.QueueRemainingSeconds = null;
            option.QueueStatusText = "Queue not loaded.";
        }
    }

    private int? ResolveTroopTrainingGroupRemainingSeconds()
    {
        var enabled = _troopTrainingBuildingOptions
            .Where(item => item.IsEnabled && item.Exists && !string.IsNullOrWhiteSpace(item.SelectedTroop))
            .ToList();
        if (enabled.Count <= 0)
        {
            return null;
        }

        if (enabled.Any(item => (item.QueueRemainingSeconds ?? 0) <= 0))
        {
            return null;
        }

        return enabled
            .Select(item => item.QueueRemainingSeconds ?? 0)
            .Where(seconds => seconds > 0)
            .DefaultIfEmpty(0)
            .Min();
    }

    private async Task RefreshTroopTrainingQueuesAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        IReadOnlyList<Building>? knownBuildings = null,
        bool refreshBuildingsBeforeRead = false)
    {
        IReadOnlyList<Building>? effectiveBuildings = knownBuildings;
        if (refreshBuildingsBeforeRead)
        {
            try
            {
                var refreshedStatus = await _botService.ReadBuildingsStatusAsync(options, AppendLog, cancellationToken);
                effectiveBuildings = refreshedStatus.Buildings;
                await Dispatcher.InvokeAsync(() =>
                {
                    _lastBuildingStatus = _lastBuildingStatus is null
                        ? refreshedStatus
                        : _lastBuildingStatus with
                        {
                            ActiveVillage = refreshedStatus.ActiveVillage,
                            Villages = refreshedStatus.Villages,
                            Tribe = refreshedStatus.Tribe,
                            Buildings = refreshedStatus.Buildings,
                            IsCapital = refreshedStatus.IsCapital,
                        };

                    ApplyTroopTrainingStatusToUi(_lastBuildingStatus);
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh troop building list before queue read: {ex.Message}");
            }
        }

        var queueStatuses = await _botService.ReadTroopTrainingQueuesAsync(options, AppendLog, effectiveBuildings, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            var effectiveStatus = _lastBuildingStatus is null
                ? null
                : _lastBuildingStatus with { TroopTrainingQueues = queueStatuses };
            if (effectiveStatus is not null)
            {
                _lastBuildingStatus = effectiveStatus;
                ApplyTroopTrainingStatusToUi(effectiveStatus);
            }
            else
            {
                ApplyTroopTrainingStatusToUi(new VillageStatus(
                    ActiveVillage: string.Empty,
                    Villages: [],
                    Resources: new Dictionary<string, string>(),
                    ResourceFields: [],
                    Buildings: effectiveBuildings?.ToList() ?? [],
                    BuildQueue: [],
                    TroopTrainingQueues: queueStatuses));
            }

            UpdateAutomationLoopRunningIndicators();
        });
    }

    private void TickTroopTrainingCountdowns()
    {
        if (_troopTrainingBuildingOptions.Count <= 0)
        {
            return;
        }

        foreach (var option in _troopTrainingBuildingOptions)
        {
            option.TickOneSecond();
        }
    }
}
