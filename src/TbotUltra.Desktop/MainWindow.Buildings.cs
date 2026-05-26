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
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly HashSet<int> WallGids = [31, 32, 33, 42, 43];
    private static readonly HashSet<int> DuplicateAllowedGids = [10, 11, 23, 38, 39];

    internal void OnLoadBuildingsClicked()
    {
        // Clear any stale pending/queued state so the upcoming snapshot is the source of truth.
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();
        _buildingDemolishingSlots.Clear();

        EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot");
        BuildingsInfoTextBlock.Text = "Queued buildings load.";
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

        // Queue upgrade-to-max for every building slot 19-40. Each task self-validates and skips
        // empty / already-max slots, so we don't need a perfectly fresh snapshot here — the load
        // task above will refresh the UI before/while these run.
        var queued = 0;
        foreach (var slotId in Enumerable.Range(19, 22))
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            };
            EnqueueQuickTask(
                "upgrade_building_to_max",
                $"Upgrade slot {slotId} to max",
                payload);
            queued++;
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

        var canDemolish = CanDemolishBuildings(out var demolishRequirementText);
        var actionsWindow = new BuildingSlotActionsWindow(row, canDemolish, demolishRequirementText)
        {
            Owner = this,
        };
        actionsWindow.UpgradeOneLevelRequested += (_, _) => QueueSingleBuildingUpgradeFromSlot(row.SlotId);
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
        if (!liveRow.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {liveRow.SlotId} is empty. Choose a building to construct.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_buildingClickCooldownBySlot.TryGetValue(liveRow.SlotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return;
        }

        _buildingClickCooldownBySlot[liveRow.SlotId] = now;
        if (liveRow.HasPendingUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"{liveRow.Name} already has a queued upgrade.";
            return;
        }

        var currentLevel = liveRow.Level ?? 0;
        var maxLevel = liveRow.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{liveRow.Name} in slot {liveRow.SlotId} is already max level ({maxLevel}).";
            return;
        }

        var targetWindow = new BuildingUpgradeTargetWindow(liveRow, maxLevel)
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
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty. Choose a building to construct.";
            return;
        }

        var currentLevel = row.Level ?? 0;
        var maxLevel = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        var pendingLevel = row.PendingTargetLevel ?? currentLevel;
        var baseLevel = Math.Max(currentLevel, pendingLevel);
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
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty.";
            return false;
        }

        var currentLevel = row.Level ?? 0;
        var maxLevel = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} in slot {slotId} is already max level ({maxLevel}).";
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

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = targetLevel.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeName] = row.Name,
        };
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
            BuildingsInfoTextBlock.Text = $"{row.Name} already has a queued upgrade to level {effectiveTargetLevel ?? targetLevel} or higher.";
            return false;
        }

        targetLevel = effectiveTargetLevel ?? targetLevel;
        _buildingLastQueuedTargetBySlot[slotId] = (targetLevel, now);
        SetPendingBuildingUpgrade(slotId, targetLevel);
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        UpgradeTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued {row.Name} in slot {slotId} to level {targetLevel}.";
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

        if (TryQueueFixedSpecialSlotConstruct(slotId))
        {
            return;
        }

        var options = GetClassifiedConstructOptionsForSlot(slotId);
        if (options.Count == 0)
        {
            BuildingsInfoTextBlock.Text = $"No constructable buildings available for slot {slotId} right now.";
            return;
        }

        ConstructSlotTextBox.Text = slotId.ToString();
        var choiceWindow = new BuildingConstructChoiceWindow(slotId, options)
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
            var maxPayload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = selected.MaxLevel.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeName] = selected.Name,
            };
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
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = clamped.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeName] = selected.Name,
            };
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

    private bool TryQueueFixedSpecialSlotConstruct(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            return false;
        }

        BuildingCatalogOption? option = null;
        if (slotId == 39)
        {
            option = BuildConstructOption(16, "Rally Point", "army_buildings");
        }
        else if (slotId == 40 && BuildingCatalogService.WallForTribe(_lastBuildingStatus.Tribe) is { } wall)
        {
            option = BuildConstructOption(wall.Gid, wall.Name, "infrastructure");
        }

        if (option is null)
        {
            return false;
        }

        return TryQueueConstructBuilding(slotId, option);
    }

    private static BuildingCatalogOption BuildConstructOption(int gid, string name, string category)
    {
        var requirements = BuildingCatalogService.RequirementsFor(gid);
        return new BuildingCatalogOption
        {
            Gid = gid,
            Name = name,
            Category = category,
            MaxLevel = BuildingCatalogService.MaxLevelFor(gid),
            RequirementEntries = requirements,
            Requirements = requirements.Count == 0
                ? "-"
                : string.Join(", ", requirements.Select(req => $"{req.Name} {req.Level}+")),
        };
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
        var occupied = projectedStatus.Buildings.FirstOrDefault(item => item.SlotId == slotId && ((item.Level ?? 0) > 0 || (item.Gid ?? 0) > 0));
        if (occupied is not null)
        {
            reason = (occupied.Level ?? 0) > 0
                ? $"Slot {slotId} is occupied by {occupied.Name} level {occupied.Level}."
                : $"Slot {slotId} is already reserved for {occupied.Name}.";
            return false;
        }

        var existingSameGidLevels = projectedStatus.Buildings
            .Where(item => item.Gid == selectedBuilding.Gid && ((item.Level ?? 0) > 0 || (item.Gid ?? 0) > 0))
            .Select(item => item.Level ?? 0)
            .ToList();
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
        else if (existingSameGidLevels.Count > 0 && !duplicateAllowed && !wallGid)
        {
            reason = $"{selectedBuilding.Name} already exists in this village.";
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

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingConstructSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingConstructGid] = selectedBuilding.Gid.ToString(),
            [BotOptionPayloadKeys.BuildingConstructName] = selectedBuilding.Name,
        };
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

        _buildingLastQueuedConstructBySlot[slotId] = (selectedBuilding.Name, now);
        SetPendingBuildingConstruct(slotId, selectedBuilding.Name);
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
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty.";
            return false;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid).ToString() : "40",
            [BotOptionPayloadKeys.BuildingUpgradeName] = row.Name,
        };
        var item = EnqueueBuildingUpgradeTaskCoalesced(
            "upgrade_building_to_max",
            payload,
            slotId,
            row.Gid is int existingGid ? BuildingCatalogService.MaxLevelFor(existingGid) : 40,
            out var effectiveTargetLevel,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} already has a queued max upgrade.";
            return false;
        }

        SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? (row.Gid is int effectiveGid ? BuildingCatalogService.MaxLevelFor(effectiveGid) : 40));
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

    private void PopulateBuildingsTab(VillageStatus status)
    {
        _buildingRows.Clear();
        _demolishableBuildings.Clear();
        var queueItems = GetActiveQueueItems();
        var projectedStatus = BuildProjectedBuildingStatus(status, queueItems);
        var queuedConstructsBySlot = GetQueuedBuildingConstructsBySlot(queueItems);
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

        var projectedBuildingBySlot = projectedStatus.Buildings
            .Where(item => item.SlotId is not null)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Level ?? 0)
                    .First());

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
            var occupied = building is not null
                && !isKnownEmpty
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
                    : null;
            var pendingConstruct = queuedConstructsBySlot.TryGetValue(slotId, out var queuedConstruct)
                ? queuedConstruct
                : _buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastConstruct)
                    ? lastConstruct.Name
                    : string.Empty;

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
            else
            {
                pendingTarget = null;
                if (projectedBuildingBySlot.TryGetValue(slotId, out var projected)
                    && (projected.Gid ?? 0) > 0
                    && string.IsNullOrWhiteSpace(pendingConstruct))
                {
                    pendingConstruct = projected.Name;
                }
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

    private static IReadOnlyDictionary<int, (double Left, double Top)> CreateBuildingSlotLayout()
    {
        const double canvasWidth = 760d;
        const double canvasHeight = 430d;
        const double slotCardWidth = 92d;
        const double centerX = (canvasWidth - slotCardWidth) / 2d;
        const double centerY = (canvasHeight - slotCardWidth) / 2d;
        const double radiusX = 300d;
        const double radiusY = 155d;

        var map = new Dictionary<int, (double Left, double Top)>();
        var slots = Enumerable.Range(19, 22).ToArray();
        for (var index = 0; index < slots.Length; index++)
        {
            var angle = (-Math.PI / 2d) + (2d * Math.PI * index / slots.Length);
            var left = centerX + (Math.Cos(angle) * radiusX);
            var top = centerY + (Math.Sin(angle) * radiusY);
            map[slots[index]] = (Math.Round(left, 1), Math.Round(top, 1));
        }

        return map;
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
            var level = Math.Max(Math.Max(fromBuildings, fromResourceFields), fromUiResourceRows);
            if (level < requirement.Level)
            {
                missing.Add(requirement);
            }
        }

        return missing;
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
