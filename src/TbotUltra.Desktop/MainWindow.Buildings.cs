using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly HashSet<int> WallGids = [31, 32, 33, 42, 43];
    private static readonly HashSet<int> DuplicateAllowedGids = [10, 11, 23, 38, 39];

    internal async Task OnLoadBuildingsClicked()
    {
        // Clear any stale pending/queued state so the upcoming snapshot is the source of truth.
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();
        _buildingDemolishingSlots.Clear();

        // When the bot is running it owns the browser session, so enqueue the snapshot to run inside the
        // loop (queuing it mid-run is safe). When it's idle/paused the queue isn't draining, so a queued
        // task would never run ("nothing happens") — read the buildings directly instead.
        var loopRunning = _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
        if (loopRunning)
        {
            EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot");
            BuildingsInfoTextBlock.Text = "Queued buildings load.";
            return;
        }

        if (BlockIfSessionSleeping("Load buildings"))
        {
            return;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen)
        {
            BuildingsInfoTextBlock.Text = "Log in first to load buildings.";
            return;
        }

        if (_uiBusy)
        {
            BuildingsInfoTextBlock.Text = "Busy — try again in a moment.";
            return;
        }

        ToggleUiBusy(true);
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var status = await ReadVillageStatusWithRetryAsync(
                options,
                CancellationToken.None,
                resourceOnly: false,
                forceCurrentVillage: false);

            SetActiveWorkingVillageFromStatus(status);
            CacheVillageStatus(status);
            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
            _resourcesViewModel.ApplyStorageForecasts(status);
            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);
            ApplyConstructionTimerFromStatus(status);
            BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"village '{status.ActiveVillage}'");
        }
        catch (Exception ex)
        {
            BuildingsInfoTextBlock.Text = $"Could not load buildings: {ex.Message}";
            AppendLog($"Load buildings failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    internal void OnUpgradeAllBuildingsToMaxClicked()
    {
        var confirm = AppDialog.Show(
            this,
            "This will queue a building snapshot refresh and then queue upgrade-to-max tasks for every building slot. Each task will validate the slot when it runs and skip empty or already maxed buildings. Continue?",
            "Upgrade all buildings to max",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        // Always refresh snapshot first so we work from current levels.
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();
        EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot");

        // Pre-filter using the currently loaded snapshot so we don't queue a full upgrade_building_to_max
        // task (each one is a costly navigation tick) for slots that are empty or already at max level.
        // Only occupied-but-not-maxed buildings (and pending constructions) are worth queueing. Each task
        // still self-validates when it runs, so a slightly stale snapshot can't cause a wrong upgrade.
        // Fallback: if no snapshot is loaded yet, queue all slots 19-40 (previous behaviour).
        var candidateSlots = _buildingRows
            .Where(row => row.SlotId is >= 19 and <= 40)
            .Where(row => (row.IsOccupied && !row.IsMaxLevel) || row.HasPendingConstruct)
            .Select(row => row.SlotId)
            .Distinct()
            .OrderBy(slotId => slotId)
            .ToList();

        var slotsToQueue = candidateSlots.Count > 0
            ? candidateSlots
            : (_buildingRows.Count == 0 ? Enumerable.Range(19, 22).ToList() : candidateSlots);

        var queued = 0;
        foreach (var slotId in slotsToQueue)
        {
            var payload = new BuildingUpgradePayload(slotId).ToDictionary();
            EnqueueQuickTask(
                "upgrade_building_to_max",
                $"Upgrade slot {slotId} to max",
                payload);
            queued++;
        }

        if (queued == 0)
        {
            BuildingsInfoTextBlock.Text = "No upgradeable buildings: every slot is empty or already at max.";
            AppendLog("Upgrade-all-to-max: nothing to queue (all slots empty or maxed).");
            return;
        }

        BuildingsInfoTextBlock.Text = $"Queued load + upgrade-to-max for {queued} slot(s).";
        AppendLog($"Upgrade-all-to-max: queued load_buildings_snapshot + {queued} upgrade_building_to_max task(s).");
    }

    private void BuildingCategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            return;
        }

        PopulateBuildingCatalogOptions(_lastBuildingStatus);
    }

    internal void BuildingSlotCircleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        if (sender is not FrameworkElement { Tag: BuildingSlotRow row })
        {
            return;
        }

        if (!row.IsOccupied && !row.HasPendingConstruct)
        {
            ShowConstructChoicesForSlot(row.SlotId);
            return;
        }

        var canDemolish = CanDemolishBuildings(out var demolishRequirementText);
        var nextLevelEstimate = BuildNextLevelEstimate(row);
        var actionsWindow = new BuildingSlotActionsWindow(row, canDemolish, demolishRequirementText, nextLevelEstimate)
        {
            Owner = this,
        };
        actionsWindow.UpgradeOneLevelRequested += (_, _) =>
        {
            QueueSingleBuildingUpgradeFromSlot(row.SlotId);
            // Re-read the live row: queueing replaced it in _buildingRows with the new pending target,
            // so the popup can show the next level's estimate without being reopened.
            var liveRow = _buildingRows.FirstOrDefault(item => item.SlotId == row.SlotId);
            if (liveRow is not null)
            {
                actionsWindow.ApplyState(liveRow, BuildNextLevelEstimate(liveRow));
            }
        };
        if (actionsWindow.ShowDialog() != true)
        {
            return;
        }

        switch (actionsWindow.SelectedAction)
        {
            case BuildingSlotAction.BuildBuilding:
                ShowConstructChoicesForSlot(row.SlotId);
                break;
            case BuildingSlotAction.Upgrade:
                ShowUpgradeTargetForSlot(row);
                break;
            case BuildingSlotAction.UpgradeToMax:
                TryQueueBuildingUpgradeToMax(row.SlotId);
                break;
            case BuildingSlotAction.Demolish:
                ShowDemolishTargetForSlot(row);
                break;
        }
    }

    private void ShowDemolishTargetForSlot(BuildingSlotRow row)
    {
        if (!CanDemolishBuildings(out var requirementText))
        {
            BuildingsInfoTextBlock.Text = requirementText;
            return;
        }

        var liveRow = _buildingRows.FirstOrDefault(item => item.SlotId == row.SlotId) ?? row;
        if (!liveRow.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {liveRow.SlotId} is empty.";
            return;
        }

        var targetWindow = new BuildingDemolishTargetWindow(liveRow)
        {
            Owner = this,
        };
        if (targetWindow.ShowDialog() != true)
        {
            return;
        }

        TryQueueBuildingDemolish(liveRow, targetWindow.SelectedTargetLevel);
    }

    private bool CanDemolishBuildings(out string requirementText)
    {
        var mainBuildingLevel = _buildingRows
            .Where(item => item.Gid == 15 || string.Equals(item.Name, "Main Building", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Level)
            .FirstOrDefault();

        if (mainBuildingLevel is >= 10)
        {
            requirementText = string.Empty;
            return true;
        }

        requirementText = mainBuildingLevel is int level
            ? $"Requires Main Building level 10 (Level: {level})"
            : "Requires Main Building level 10.";
        return false;
    }

    private void ShowUpgradeTargetForSlot(BuildingSlotRow row)
    {
        var liveRow = _buildingRows.FirstOrDefault(item => item.SlotId == row.SlotId) ?? row;
        if (!liveRow.CanQueueUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"Slot {liveRow.SlotId} is empty. Choose a building to construct.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryBeginSlotClick(_buildingClickCooldownBySlot, liveRow.SlotId, now))
        {
            return;
        }

        var currentLevel = liveRow.UpgradeBaseLevel;
        var maxLevel = liveRow.UpgradeGid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{liveRow.UpgradeName} in slot {liveRow.SlotId} is already max level ({maxLevel}).";
            return;
        }

        var targetWindow = new BuildingUpgradeTargetWindow(
            liveRow,
            maxLevel,
            targetLevel => BuildUpgradeRangeEstimate(liveRow, targetLevel))
        {
            Owner = this,
        };
        if (targetWindow.ShowDialog() != true)
        {
            return;
        }

        _ = TryQueueBuildingUpgradeToLevel(liveRow.SlotId, targetWindow.SelectedTargetLevel);
    }

    private void QueueSingleBuildingUpgradeFromSlot(int slotId)
    {
        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.CanQueueUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty. Choose a building to construct.";
            return;
        }

        var maxLevel = row.UpgradeGid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        var baseLevel = row.UpgradeBaseLevel;
        var targetLevel = Math.Clamp(baseLevel + 1, 1, maxLevel);
        _ = TryQueueBuildingUpgradeToLevel(slotId, targetLevel);
    }

    private static bool IsRallyPointSlot(int slotId) => slotId == 39;

    private static bool IsRallyPointGid(int gid) => gid == 16;

    private bool TryQueueBuildingUpgradeToLevel(int slotId, int targetLevel)
    {
        if (targetLevel < 1)
        {
            BuildingsInfoTextBlock.Text = "Target level must be an integer >= 1.";
            return false;
        }

        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.CanQueueUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty.";
            return false;
        }

        var currentLevel = row.UpgradeBaseLevel;
        var maxLevel = row.UpgradeGid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{row.UpgradeName} in slot {slotId} is already max level ({maxLevel}).";
            return false;
        }

        targetLevel = Math.Clamp(targetLevel, currentLevel + 1, maxLevel);
        var now = DateTimeOffset.UtcNow;
        if (_buildingLastQueuedTargetBySlot.TryGetValue(slotId, out var lastQueued)
            && lastQueued.Target == targetLevel
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return false;
        }

        var payload = new BuildingUpgradePayload(slotId, targetLevel, row.UpgradeName).ToDictionary();
        var item = EnqueueBuildingUpgradeTaskCoalesced(
            "upgrade_building_to_level",
            payload,
            slotId,
            targetLevel,
            out var effectiveTargetLevel,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{row.UpgradeName} already has a queued upgrade to level {effectiveTargetLevel ?? targetLevel} or higher.";
            return false;
        }

        targetLevel = effectiveTargetLevel ?? targetLevel;
        _buildingLastQueuedTargetBySlot[slotId] = (targetLevel, now);
        SetPendingBuildingUpgrade(slotId, targetLevel);
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        UpgradeTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued {row.UpgradeName} in slot {slotId} to level {targetLevel}.";
        var removedSuffix = removedCount > 0 ? $" (replaced {removedCount} pending item(s))" : string.Empty;
        AppendLog($"Queued single building upgrade: slot {slotId} -> level {targetLevel}{removedSuffix}.");
        return true;
    }

    private void ShowConstructChoicesForSlot(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        var options = GetClassifiedConstructOptionsForSlot(slotId);
        if (options.Count == 0)
        {
            BuildingsInfoTextBlock.Text = $"No constructable buildings available for slot {slotId} right now.";
            return;
        }

        ConstructSlotTextBox.Text = slotId.ToString();
        var choiceWindow = new BuildingConstructChoiceWindow(
            slotId,
            options,
            (gid, targetLevel) => BuildConstructRangeEstimate(gid, targetLevel))
        {
            Owner = this,
        };
        if (choiceWindow.ShowDialog() != true || choiceWindow.SelectedOption is null)
        {
            return;
        }

        var selected = choiceWindow.SelectedOption;
        var targetLevel = choiceWindow.SelectedTargetLevel;
        if (!TryQueueConstructBuilding(slotId, selected))
        {
            return;
        }

        if (targetLevel == 0)
        {
            // Slot is still empty at this moment (construct hasn't run yet), so we queue
            // upgrade-to-max directly instead of going through TryQueueBuildingUpgradeToMax
            // which gates on IsOccupied.
            var maxPayload = new BuildingUpgradePayload(slotId, selected.MaxLevel, selected.Name).ToDictionary();
            var queuedMax = EnqueueBuildingUpgradeTaskCoalesced(
                "upgrade_building_to_max",
                maxPayload,
                slotId,
                selected.MaxLevel,
                out var effectiveTargetLevel,
                out var enqueued,
                out _);
            if (enqueued)
            {
                SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? selected.MaxLevel);
                RequestQueueUiRefresh(selectId: queuedMax?.Id);
                TriggerQueueAutoRunFromEnqueue();
            }
            BuildingsInfoTextBlock.Text = $"Queued construct + upgrade to max for {selected.Name} in slot {slotId}.";
        }
        else if (targetLevel > 1)
        {
            var clamped = Math.Clamp(targetLevel, 1, selected.MaxLevel);
            var payload = new BuildingUpgradePayload(slotId, clamped, selected.Name).ToDictionary();
            var queuedUpgrade = EnqueueBuildingUpgradeTaskCoalesced(
                "upgrade_building_to_level",
                payload,
                slotId,
                clamped,
                out var effectiveTargetLevel,
                out var enqueued,
                out _);
            if (enqueued)
            {
                SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? clamped);
                RequestQueueUiRefresh(selectId: queuedUpgrade?.Id);
                TriggerQueueAutoRunFromEnqueue();
            }
            BuildingsInfoTextBlock.Text = $"Queued construct + upgrade to level {clamped} for {selected.Name} in slot {slotId}.";
        }
    }

    private IReadOnlyList<BuildingCatalogOption> GetConstructableOptionsForSlot(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            return [];
        }

        return _buildingCatalogOptions
            .Where(option => CanQueueConstructBuilding(slotId, option, out _))
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    private static string NormalizeBuildingName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim().ToLowerInvariant();
        return trimmed.Replace("'", string.Empty).Replace("’", string.Empty);
    }

    private IReadOnlyList<BuildingCatalogOption> GetClassifiedConstructOptionsForSlot(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            return [];
        }

        var status = BuildProjectedBuildingStatus(_lastBuildingStatus);
        var fullCatalog = BuildingCatalogService.GetFullCatalog(status.Tribe);
        var occupiedBuildings = status.Buildings
            .Where(b => (b.Level ?? 0) > 0)
            .ToList();
        var existingGids = occupiedBuildings
            .Where(b => b.Gid is not null)
            .Select(b => b.Gid!.Value)
            .ToHashSet();
        var existingNames = occupiedBuildings
            .Select(b => NormalizeBuildingName(b.Name))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wallNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "city wall", "earth wall", "palisade", "stone wall", "makeshift wall",
        };
        var anyWallExists = existingGids.Any(g => WallGids.Contains(g))
            || existingNames.Any(n => wallNames.Contains(n));
        var result = new List<BuildingCatalogOption>(fullCatalog.Count);

        foreach (var entry in fullCatalog)
        {
            var maxLevel = BuildingCatalogService.MaxLevelFor(entry.Gid);
            var option = new BuildingCatalogOption
            {
                Gid = entry.Gid,
                Name = entry.Name,
                Category = entry.Category,
                IsSpecial = entry.IsSpecial,
                Tribe = entry.RequiredTribe,
                MaxLevel = maxLevel,
                RequirementEntries = entry.Requirements,
                Requirements = entry.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", entry.Requirements.Select(req => $"{req.Name} {req.Level}+")),
            };

            if (entry.IsSpecial && !entry.MatchesPlayerTribe)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = string.IsNullOrEmpty(entry.RequiredTribe)
                    ? "Wrong tribe"
                    : $"Only available for {entry.RequiredTribe}";
                result.Add(option);
                continue;
            }

            // World Wonder, Great Warehouse, Great Granary require building plans — not yet supported.
            if (entry.Gid is 38 or 39 or 40)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = entry.Gid == 40
                    ? "World Wonder requires building plans"
                    : $"{entry.Name} requires building plans";
                result.Add(option);
                continue;
            }

            // Great Barracks (29) and Great Stable (30) cannot be built in the capital village.
            if ((entry.Gid is 29 or 30) && status.IsCapital == true)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = $"{entry.Name} cannot be built in the capital";
                result.Add(option);
                continue;
            }

            // Palace (26) conflicts with Residence (25) and Command Center (44) — only one allowed per village.
            if (entry.Gid == 26 && (existingGids.Contains(25) || existingGids.Contains(44)))
            {
                var conflicting = existingGids.Contains(25) ? "Residence" : "Command Center";
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{conflicting} already exists in this village";
                result.Add(option);
                continue;
            }
            // Residence (25) conflicts with Palace (26) and Command Center (44) symmetrically.
            if (entry.Gid == 25 && (existingGids.Contains(26) || existingGids.Contains(44)))
            {
                var conflicting = existingGids.Contains(26) ? "Palace" : "Command Center";
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{conflicting} already exists in this village";
                result.Add(option);
                continue;
            }
            // Command Center (44) conflicts with Palace (26) and Residence (25).
            if (entry.Gid == 44 && (existingGids.Contains(25) || existingGids.Contains(26)))
            {
                var conflicting = existingGids.Contains(25) ? "Residence" : "Palace";
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{conflicting} already exists in this village";
                result.Add(option);
                continue;
            }

            var isWall = WallGids.Contains(entry.Gid);
            var isRallyPoint = IsRallyPointGid(entry.Gid);
            const int rallyPointSlotId = 39;
            const int wallSlotId = 40;
            if (isRallyPoint && slotId != rallyPointSlotId)
            {
                continue;
            }
            if (!isRallyPoint && slotId == rallyPointSlotId)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = "Slot 39 is the Rally Point slot";
                result.Add(option);
                continue;
            }
            if (isWall && slotId != wallSlotId)
            {
                // Walls can only be built on slot 40 — hide from other slots.
                continue;
            }
            if (!isWall && slotId == wallSlotId)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = "Slot 40 is the wall slot";
                result.Add(option);
                continue;
            }
            var matchesByName = existingNames.Contains(NormalizeBuildingName(entry.Name));
            var alreadyBuilt = ((existingGids.Contains(entry.Gid) || matchesByName)
                    && !DuplicateAllowedGids.Contains(entry.Gid) && !isWall)
                || (isWall && anyWallExists);
            if (alreadyBuilt)
            {
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = isWall
                    ? "Wall already built in this village"
                    : "Already built in this village";
                result.Add(option);
                continue;
            }

            if (CanQueueConstructBuilding(slotId, option, out var reason))
            {
                option.Availability = BuildingConstructAvailability.Available;
            }
            else
            {
                var missing = MissingRequirements(status, option.RequirementEntries);
                if (missing.Count > 0)
                {
                    option.Availability = BuildingConstructAvailability.Locked;
                    option.MissingRequirements = missing;
                }
                else
                {
                    option.Availability = BuildingConstructAvailability.Unavailable;
                    option.UnavailableReason = reason;
                }
            }

            result.Add(option);
        }

        return result;
    }

    private bool CanQueueConstructBuilding(int slotId, BuildingCatalogOption selectedBuilding, out string reason)
    {
        reason = string.Empty;
        if (_lastBuildingStatus is null)
        {
            reason = "Load buildings first.";
            return false;
        }

        var projectedStatus = BuildProjectedBuildingStatus(_lastBuildingStatus);
        var occupied = projectedStatus.Buildings.FirstOrDefault(item => item.SlotId == slotId
            && ((item.Level ?? 0) > 0
                || ((item.Gid ?? 0) > 0 && !IsUnbuiltFixedSpecialSlot(slotId, item, selectedBuilding.Gid))));
        if (occupied is not null)
        {
            reason = (occupied.Level ?? 0) > 0
                ? $"Slot {slotId} is occupied by {occupied.Name} level {occupied.Level}."
                : $"Slot {slotId} is already reserved for {occupied.Name}.";
            return false;
        }

        // Built copies of this building (level > 0) — drives the "duplicate after level N" thresholds.
        var existingSameGidLevels = projectedStatus.Buildings
            .Where(item => item.Gid == selectedBuilding.Gid && (item.Level ?? 0) > 0)
            .Select(item => item.Level ?? 0)
            .ToList();
        // True when this building is already built OR currently queued as constructing in this village
        // (a queued construct projects as level 0, so it is NOT in existingSameGidLevels). Used to block
        // a second copy of a non-duplicatable building (e.g. Hero's Mansion) while one is in the queue.
        var sameGidAlreadyPresent = projectedStatus.Buildings.Any(item => item.Gid == selectedBuilding.Gid);
        var duplicateAllowed = selectedBuilding.Gid is 23 or 38 or 39;
        var wallGid = selectedBuilding.Gid is 31 or 32 or 33 or 42 or 43;
        var rallyPointGid = IsRallyPointGid(selectedBuilding.Gid);
        if (rallyPointGid && slotId != 39)
        {
            reason = "Rally Point can only be built on slot 39.";
            return false;
        }

        if (!rallyPointGid && slotId == 39)
        {
            reason = "Slot 39 is the Rally Point slot.";
            return false;
        }

        if (wallGid && slotId != 40)
        {
            reason = "Wall can only be built on slot 40.";
            return false;
        }

        if (!wallGid && slotId == 40)
        {
            reason = "Slot 40 is the wall slot.";
            return false;
        }

        if (selectedBuilding.Gid is 10 or 11)
        {
            if (existingSameGidLevels.Count > 0)
            {
                var currentHighest = existingSameGidLevels.Max();
                if (currentHighest < 20)
                {
                    reason = $"{selectedBuilding.Name} can only be duplicated after an existing one reaches level 20.";
                    return false;
                }
            }
        }
        else if (selectedBuilding.Gid == 23)
        {
            if (existingSameGidLevels.Count > 0)
            {
                var currentHighest = existingSameGidLevels.Max();
                if (currentHighest < 10)
                {
                    reason = $"{selectedBuilding.Name} can only be duplicated after an existing one reaches level 10.";
                    return false;
                }
            }
        }
        else if (sameGidAlreadyPresent && !duplicateAllowed && !wallGid)
        {
            reason = $"{selectedBuilding.Name} already exists or is queued in this village.";
            return false;
        }

        var missing = MissingRequirements(projectedStatus, selectedBuilding.RequirementEntries);
        if (missing.Count > 0)
        {
            reason = $"Missing requirements: {string.Join(", ", missing.Select(item => $"{item.Name} {item.Level}+"))}";
            return false;
        }

        return true;
    }

    private static bool IsUnbuiltFixedSpecialSlot(int slotId, Building building, int selectedGid)
    {
        if ((building.Level ?? 0) > 0 || building.Gid != selectedGid)
        {
            return false;
        }

        return (slotId == 39 && IsRallyPointGid(selectedGid))
            || (slotId == 40 && WallGids.Contains(selectedGid));
    }

    private static bool IsUnbuiltFixedSpecialBuilding(int slotId, Building building)
    {
        if ((building.Level ?? 0) > 0 || building.Gid is not int gid)
        {
            return false;
        }

        return (slotId == 39 && IsRallyPointGid(gid))
            || (slotId == 40 && WallGids.Contains(gid));
    }

    private bool TryQueueConstructBuilding(int slotId, BuildingCatalogOption selectedBuilding)
    {
        if (!CanQueueConstructBuilding(slotId, selectedBuilding, out var reason))
        {
            BuildingsInfoTextBlock.Text = reason;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastQueued)
            && string.Equals(lastQueued.Name, selectedBuilding.Name, StringComparison.OrdinalIgnoreCase)
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return false;
        }

        var payload = new BuildingConstructPayload(slotId, selectedBuilding.Gid, selectedBuilding.Name).ToDictionary();
        var item = EnqueueBuildingConstructTaskCoalesced(
            payload,
            slotId,
            selectedBuilding.Gid,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{selectedBuilding.Name} is already queued for slot {slotId}.";
            return false;
        }

        _buildingLastQueuedConstructBySlot[slotId] = (selectedBuilding.Name, selectedBuilding.Gid, now);
        SetPendingBuildingConstruct(slotId, selectedBuilding.Name, selectedBuilding.Gid);
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        ConstructSlotTextBox.Text = slotId.ToString();
        ConstructBuildingComboBox.SelectedItem = _buildingCatalogOptions.FirstOrDefault(item => item.Gid == selectedBuilding.Gid);
        BuildingsInfoTextBlock.Text = $"Queued construct: {selectedBuilding.Name} in slot {slotId}.";
        var removedSuffix = removedCount > 0 ? $" (replaced {removedCount} pending item(s))" : string.Empty;
        AppendLog($"Queued building construct: slot {slotId} -> {selectedBuilding.Name}{removedSuffix}.");
        return true;
    }


    private void QueueConstructBuildingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        if (!int.TryParse(ConstructSlotTextBox.Text.Trim(), out var slotId) || slotId < 19)
        {
            BuildingsInfoTextBlock.Text = "Construct slot must be an integer >= 19.";
            return;
        }

        if (ConstructBuildingComboBox.SelectedItem is not BuildingCatalogOption selectedBuilding)
        {
            BuildingsInfoTextBlock.Text = "Select a building to construct.";
            return;
        }

        _ = TryQueueConstructBuilding(slotId, selectedBuilding);
    }

    private void QueueUpgradeBuildingMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UpgradeSlotTextBox.Text.Trim(), out var slotId) || slotId < 19)
        {
            BuildingsInfoTextBlock.Text = "Upgrade slot must be an integer >= 19.";
            return;
        }

        TryQueueBuildingUpgradeToMax(slotId);
    }

    private bool TryQueueBuildingUpgradeToMax(int slotId)
    {
        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.CanQueueUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty.";
            return false;
        }

        var maxLevel = row.UpgradeGid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        var payload = new BuildingUpgradePayload(slotId, maxLevel, row.UpgradeName).ToDictionary();
        var item = EnqueueBuildingUpgradeTaskCoalesced(
            "upgrade_building_to_max",
            payload,
            slotId,
            maxLevel,
            out var effectiveTargetLevel,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{row.UpgradeName} already has a queued max upgrade.";
            return false;
        }

        SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? maxLevel);
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        BuildingsInfoTextBlock.Text = $"Queued max-upgrade for slot {slotId}.";
        if (removedCount > 0)
        {
            AppendLog($"Queued building max-upgrade: slot {slotId} (replaced {removedCount} pending item(s)).");
        }
        return true;
    }

    private bool TryQueueBuildingDemolish(BuildingSlotRow row, int targetLevel)
    {
        if (!row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {row.SlotId} is empty.";
            return false;
        }

        if (targetLevel < 0)
        {
            BuildingsInfoTextBlock.Text = "Demolish target level must be an integer >= 0.";
            return false;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = row.SlotId.ToString(),
            [BotOptionPayloadKeys.TargetLevel] = targetLevel.ToString(),
        };
        EnqueueQuickTask("demolish_building_to_level", $"Demolish {row.Name} to level {targetLevel}", payload);
        SetDemolishingFlag(row.SlotId, true);
        DemolishBuildingComboBox.SelectedItem = _demolishableBuildings.FirstOrDefault(item => item.SlotId == row.SlotId);
        DemolishTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued demolition for {row.Name} (slot {row.SlotId}) to level {targetLevel}.";
        return true;
    }

    private string GetBuildingsSnapshotPathForActiveAccount()
    {
        var account = _accountStore.ActiveAccountName();
        return AccountStoragePaths.BuildingsSnapshotPath(_projectRoot, account);
    }

    private async Task LoadBuildingsSnapshotIntoUiAsync(CancellationToken cancellationToken)
    {
        var snapshotPath = GetBuildingsSnapshotPathForActiveAccount();
        if (!File.Exists(snapshotPath))
        {
            AppendLog("Buildings snapshot not found.");
            return;
        }

        var json = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<BuildingSnapshotDto>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        if (snapshot is null)
        {
            AppendLog("Buildings snapshot could not be parsed.");
            return;
        }

        var status = new VillageStatus(
            ActiveVillage: snapshot.ActiveVillage ?? string.Empty,
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: (snapshot.ResourceFields ?? [])
                .Select(item => new ResourceField(
                    item.SlotId,
                    item.FieldType ?? string.Empty,
                    item.Name ?? string.Empty,
                    item.Level,
                    item.Url))
                .ToList(),
            Buildings: (snapshot.Buildings ?? [])
                .Select(item =>
                {
                    var name = string.IsNullOrWhiteSpace(item.Name) || string.Equals(item.Name, "g0", StringComparison.OrdinalIgnoreCase)
                        ? "Empty"
                        : item.Name!;
                    var gid = item.Gid is > 0 ? item.Gid : null;
                    var level = gid is null && string.Equals(name, "Empty", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : item.Level;
                    return new Building(item.SlotId, name, level, item.Url, gid);
                })
                .ToList(),
            BuildQueue: [],
            Tribe: snapshot.Tribe ?? "Unknown",
            VillageCount: 0,
            IsCapital: snapshot.IsCapital);

        _lastBuildingStatus = status;
        await Dispatcher.InvokeAsync(() =>
        {
            ApplyTroopsAvailabilityFromVillageStatus(status);
            PopulateBuildingsTab(status);
            BuildingsInfoTextBlock.Text = $"Loaded {status.Buildings.Count} building slots from queue snapshot.";
        });
    }

    private sealed record BuildingSnapshotDto(
        string? Account,
        string? ActiveVillage,
        string? Tribe,
        bool? IsCapital,
        List<BuildingSnapshotItemDto>? Buildings,
        List<ResourceFieldSnapshotItemDto>? ResourceFields);

    private sealed record BuildingSnapshotItemDto(
        int? SlotId,
        string? Name,
        int? Level,
        string? Url,
        int? Gid);

    private sealed record ResourceFieldSnapshotItemDto(
        int? SlotId,
        string? FieldType,
        string? Name,
        int? Level,
        string? Url);

    // Matches Travian's in-progress construction list (upgrades started outside the program) to known
    // slots by normalized name + (target level - 1), returning slot -> target level. Only UNAMBIGUOUS
    // matches are returned: if a construction could map to more than one slot (e.g. several croplands at
    // the same level), it is dropped rather than guessing the wrong field. Construct (new building)
    // entries are ignored — only upgrades of an existing slot are surfaced.
    private static Dictionary<int, int> BuildExternalUpgradeTargetsBySlot(
        IReadOnlyList<ActiveConstruction>? activeConstructions,
        ConstructionKind kind,
        IEnumerable<(int Slot, string? Name, int? Level)> slots)
    {
        var result = new Dictionary<int, int>();
        if (activeConstructions is null || activeConstructions.Count == 0)
        {
            return result;
        }

        var slotList = slots.ToList();
        var ambiguousSlots = new HashSet<int>();
        foreach (var construction in activeConstructions)
        {
            if (construction.Kind != kind || construction.Level is not int target || target < 1)
            {
                continue;
            }

            var name = NormalizeConstructionName(construction.Name);
            if (name.Length == 0)
            {
                continue;
            }

            var candidateSlots = slotList
                .Where(slot => (slot.Level ?? 0) == target - 1
                    && string.Equals(NormalizeConstructionName(slot.Name), name, StringComparison.Ordinal))
                .Select(slot => slot.Slot)
                .ToList();

            // Skip when we cannot tell exactly which field/building is being upgraded.
            if (candidateSlots.Count != 1)
            {
                continue;
            }

            var slotId = candidateSlots[0];
            if (result.ContainsKey(slotId))
            {
                // Two constructions resolve to the same slot — ambiguous, drop it.
                ambiguousSlots.Add(slotId);
                continue;
            }

            result[slotId] = target;
        }

        foreach (var slotId in ambiguousSlots)
        {
            result.Remove(slotId);
        }

        return result;
    }

    private static string NormalizeConstructionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim().ToLowerInvariant()
            .Replace("'", string.Empty)
            .Replace("’", string.Empty)
            .Replace(".", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
    }

    private void PopulateBuildingsTab(VillageStatus status)
    {
        // Keep the Buildings tab locked to the village the user is viewing in the dropdown. A background
        // read for a different (active) village must not blow away the selected village's building view
        // that the user is queueing from. Indeterminate village → repaint (never blank defensively).
        if (!IsStatusForSelectedVillage(status))
        {
            return;
        }

        _buildingRows.Clear();
        _demolishableBuildings.Clear();
        // Only this village's queued work may color/disable its slots. The queue is one-per-account with
        // every item tagged for its target village, so without this filter another village's queued
        // upgrades light up the same slot numbers here (slots 19-40 exist in every village) and the slot
        // looks "already queued" so it can't be clicked. Global/untagged items still apply everywhere.
        var queueItems = GetActiveQueueItems()
            .Where(IsQueueItemForSelectedVillageOrGlobal)
            .ToList();
        var queuedConstructsBySlot = GetQueuedBuildingConstructsBySlot(queueItems);
        var queuedConstructGidsBySlot = GetQueuedBuildingConstructGidsBySlot(queueItems);
        var queuedTargetsBySlot = GetQueuedBuildingTargetsBySlot(queueItems);

        var categoryByGid = BuildingCatalogService.GetCatalogForTribe(status.Tribe)
            .ToDictionary(item => item.Gid, item => item, EqualityComparer<int>.Default);

        var buildingBySlot = status.Buildings
            .Where(item => item.SlotId is not null)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Level ?? 0)
                    .First());

        // Upgrades started outside the program (Travian's own build list) so they show the target
        // level in parentheses just like program-queued upgrades.
        var externalUpgradeTargetsBySlot = BuildExternalUpgradeTargetsBySlot(
            status.ActiveConstructions,
            ConstructionKind.Building,
            buildingBySlot.Select(kv => (kv.Key, (string?)kv.Value.Name, kv.Value.Level)));

        var occupiedCount = 0;
        foreach (var slotId in Enumerable.Range(19, 22))
        {
            buildingBySlot.TryGetValue(slotId, out var building);
            var isKnownEmpty = building is null || IsEmptyBuilding(building);
            var hasIdentifiedBuildingName = building is not null
                && !string.IsNullOrWhiteSpace(building.Name)
                && !string.Equals(building.Name, "Unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase)
                && !building.Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
            var isUnbuiltFixedSpecial = building is not null && IsUnbuiltFixedSpecialBuilding(slotId, building);
            var occupied = building is not null
                && !isKnownEmpty
                && !isUnbuiltFixedSpecial
                && ((building.Level ?? 0) > 0
                    || (building.Gid ?? 0) > 0
                    || hasIdentifiedBuildingName);

            var category = occupied ? "infrastructure" : "-";
            var requirements = occupied ? string.Empty : "-";
            if (occupied && building!.Gid is int gid && categoryByGid.TryGetValue(gid, out var catalog))
            {
                category = catalog.Category;
                requirements = catalog.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", catalog.Requirements.Select(item => $"{item.Name} {item.Level}+"));
            }

            int? pendingTarget = queuedTargetsBySlot.TryGetValue(slotId, out var queuedTarget)
                ? queuedTarget
                : _buildingLastQueuedTargetBySlot.TryGetValue(slotId, out var lastTarget)
                    ? lastTarget.Target
                    : externalUpgradeTargetsBySlot.TryGetValue(slotId, out var externalTarget)
                        ? externalTarget
                        : null;
            var pendingConstruct = queuedConstructsBySlot.TryGetValue(slotId, out var queuedConstruct)
                ? queuedConstruct
                : _buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastConstruct)
                    ? lastConstruct.Name
                    : string.Empty;
            var pendingConstructGid = queuedConstructGidsBySlot.TryGetValue(slotId, out var queuedConstructGid)
                ? queuedConstructGid
                : _buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastConstructGid)
                    ? lastConstructGid.Gid
                    : (int?)null;

            if (!BuildingSlotLayoutById.TryGetValue(slotId, out var layout))
            {
                layout = (0d, 0d);
            }

            if (occupied)
            {
                occupiedCount += 1;
                if (pendingTarget is int pendingQueuedTarget && pendingQueuedTarget <= (building!.Level ?? 0))
                {
                    pendingTarget = null;
                }

                pendingConstruct = string.Empty;
            }
            string slotName;
            int? slotLevel;
            int? slotGid;
            var isWallSlot = slotId == 40;
            var isRallyPointSlot = IsRallyPointSlot(slotId);
            if (occupied)
            {
                slotName = building!.Name;
                slotLevel = building.Level;
                slotGid = building.Gid;
            }
            else if (isWallSlot || isRallyPointSlot)
            {
                slotName = isRallyPointSlot
                    ? "Rally Point"
                    : BuildingCatalogService.WallForTribe(status.Tribe)?.Name ?? "Wall";
                slotLevel = 0;
                slotGid = null;
            }
            else
            {
                slotName = "Empty";
                slotLevel = null;
                slotGid = null;
            }

            // If a demolish has actually completed (slot is now empty), drop the in-progress flag.
            if (!occupied && _buildingDemolishingSlots.Contains(slotId))
            {
                _buildingDemolishingSlots.Remove(slotId);
            }

            var row = new BuildingSlotRow
            {
                SlotId = slotId,
                Name = slotName,
                Level = slotLevel,
                Gid = slotGid,
                Category = category,
                Requirements = requirements,
                PendingTargetLevel = pendingTarget,
                PendingConstructName = pendingConstruct,
                PendingConstructGid = pendingConstructGid,
                IsDemolishing = _buildingDemolishingSlots.Contains(slotId),
                MapLeft = layout.Left,
                MapTop = layout.Top,
                IsWallSlot = isWallSlot,
                IsRallyPointSlot = isRallyPointSlot,
            };
            _buildingRows.Add(row);

            if (occupied)
            {
                _demolishableBuildings.Add(row);
            }
        }

        PopulateBuildingCatalogOptions(status);
        BuildingsInfoTextBlock.Text = $"Buildings loaded. Occupied slots: {occupiedCount}, free slots: {22 - occupiedCount}.";

        // The Main Building level just became available for this village. Recompute the queue estimates
        // so already-queued items reflect the build-time discount. Skipped when this call came from
        // RefreshQueueUi itself (see _isRefreshingQueueUi) to avoid an endless refresh loop.
        if (!_isRefreshingQueueUi)
        {
            RequestQueueUiRefresh();
        }
    }

    private static bool IsEmptyBuilding(Building building)
    {
        return (building.Gid ?? 0) <= 0
            && ((building.Level ?? 0) <= 0
                || string.IsNullOrWhiteSpace(building.Name)
                || string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase));
    }

    private void PopulateBuildingCatalogOptions(VillageStatus status)
    {
        _buildingCatalogOptions.Clear();

        var categoryFilter = (BuildingCategoryComboBox.SelectedItem as string)?.Trim().ToLowerInvariant() ?? "all";
        var catalog = BuildingCatalogService.GetCatalogForTribe(status.Tribe);
        foreach (var item in catalog)
        {
            if (!string.Equals(categoryFilter, "all", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var option = new BuildingCatalogOption
            {
                Gid = item.Gid,
                Name = item.Name,
                Category = item.Category,
                IsSpecial = item.IsSpecial,
                RequirementEntries = item.Requirements,
                Requirements = item.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", item.Requirements.Select(req => $"{req.Name} {req.Level}+")),
            };
            _buildingCatalogOptions.Add(option);
        }

        if (_buildingCatalogOptions.Count > 0)
        {
            ConstructBuildingComboBox.SelectedIndex = 0;
        }
    }

    private List<BuildingRequirementEntry> MissingRequirements(VillageStatus status, IReadOnlyList<BuildingRequirementEntry> requirements)
    {
        var projectedStatus = BuildProjectedBuildingStatus(status);
        var missing = new List<BuildingRequirementEntry>();
        foreach (var requirement in requirements)
        {
            var fromBuildings = projectedStatus.Buildings
                .Where(item => item.Level is not null && item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var fromResourceFields = status.ResourceFields
                .Where(item => item.Level is not null
                    && (item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase)
                        || (item.FieldType?.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var fromUiResourceRows = MaxLevelInUiResourceRows(requirement.Name);
            // A queued (not yet built) construct of the required building counts as level 1: constructing
            // a building yields level 1 in-game, so queuing a prerequisite building should already unlock
            // the dependent one. Construct+upgrade chains are covered by projectedStatus above (the upgrade
            // projects the target level onto the constructed slot); this only adds the lone-construct case.
            var fromQueuedConstructs = QueuedConstructProvidesRequirement(requirement.Name) ? 1 : 0;
            var level = Math.Max(Math.Max(fromBuildings, fromResourceFields), Math.Max(fromUiResourceRows, fromQueuedConstructs));
            if (level < requirement.Level)
            {
                missing.Add(requirement);
            }
        }

        return missing;
    }

    private bool QueuedConstructProvidesRequirement(string requirementName)
    {
        if (string.IsNullOrWhiteSpace(requirementName))
        {
            return false;
        }

        return GetActiveQueueItems()
            .Where(IsQueueItemForSelectedVillageOrGlobal)
            .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .Any(item => TryReadBuildingConstructPayload(item.Payload, out _, out _, out var name)
                && !string.IsNullOrWhiteSpace(name)
                && name.Contains(requirementName, StringComparison.OrdinalIgnoreCase));
    }

    private int MaxLevelInUiResourceRows(string requirementName)
    {
        // Resource field requirements (Cropland, Iron Mine, Clay Pit, Woodcutter) live on the
        // Resources tab — buildings snapshot doesn't carry them. Look them up directly.
        IEnumerable<ResourceFieldRow> rows = requirementName switch
        {
            var n when n.Contains("Wood", StringComparison.OrdinalIgnoreCase) => _resourcesViewModel.WoodFields,
            var n when n.Contains("Clay", StringComparison.OrdinalIgnoreCase) => _resourcesViewModel.ClayFields,
            var n when n.Contains("Iron", StringComparison.OrdinalIgnoreCase) => _resourcesViewModel.IronFields,
            var n when n.Contains("Crop", StringComparison.OrdinalIgnoreCase) => _resourcesViewModel.CroplandFields,
            _ => [],
        };
        return rows
            .Where(r => r.Level is not null)
            .Select(r => r.Level!.Value)
            .DefaultIfEmpty(0)
            .Max();
    }
}
