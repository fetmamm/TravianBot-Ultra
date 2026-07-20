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
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static HashSet<int> WallGids => BuildingsViewModel.WallGids;
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
            EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot", priority: 100);
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
                _loopController.AcquireSessionScopeToken(),
                resourceOnly: false,
                forceCurrentVillage: false);

            SetActiveWorkingVillageFromStatus(status);
            CacheVillageStatus(status);
            var statusForSelectedVillage = IsStatusForSelectedVillage(status);
            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
            if (statusForSelectedVillage)
            {
                _resourcesViewModel.ApplyStorageForecasts(status);
                _lastBuildingStatus = status;
                PopulateBuildingsTab(status);
                ApplyConstructionTimerFromStatus(status);
                BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"village '{status.ActiveVillage}'");
            }
            else
            {
                AppendLog($"[storage-refresh] skipped Load buildings storage repaint: data is for '{status.ActiveVillage}', another village is selected.");
                BuildingsInfoTextBlock.Text = "The browser returned another village. Select Refresh to try again.";
            }
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

        var status = ResolveSelectedVillageBuildingStatus();
        if (status is null || status.Buildings.Count == 0 || _buildingRows.Count == 0)
        {
            BuildingsInfoTextBlock.Text = "Load buildings for the selected village before queuing upgrade-all-to-max.";
            return;
        }

        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();

        var candidateRows = _buildingRows
            .Where(row => row.SlotId is >= 19 and <= 40)
            .Where(row => (row.IsOccupied && !row.IsMaxLevel) || row.HasPendingConstruct)
            .GroupBy(row => row.SlotId)
            .Select(group => group.First())
            .OrderBy(row => row.SlotId)
            .ToList();
        if (candidateRows.Count == 0)
        {
            BuildingsInfoTextBlock.Text = "No upgradeable buildings: every slot is empty or already at max.";
            AppendLog("Upgrade-all-to-max: nothing to queue (all slots empty or maxed).");
            return;
        }

        var requested = candidateRows.Select(row =>
        {
            var maxLevel = row.UpgradeGid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 20;
            var payload = new BuildingUpgradePayload(row.SlotId, maxLevel, row.UpgradeName).ToDictionary();
            ApplySelectedVillageToPayload(payload);
            return new QueueItemCreateRequest("upgrade_building_to_max", payload, 0, 3);
        }).ToList();
        if (!TryPrepareConstructionStoragePreflight(requested, out var plannedRequests, out var storageUpgrades))
        {
            return;
        }

        var refreshPayload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ApplySelectedVillageToPayload(refreshPayload);
        var finalRequests = new List<QueueItemCreateRequest>
        {
            new("load_buildings_snapshot", refreshPayload, 0, 3),
        };
        finalRequests.AddRange(plannedRequests);
        var created = _botService.EnqueueBatch(finalRequests);
        ApplyStoragePreflightPendingState(storageUpgrades);
        foreach (var row in candidateRows)
        {
            var maxLevel = row.UpgradeGid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 20;
            SetPendingBuildingUpgrade(row.SlotId, maxLevel);
        }
        RequestQueueUiRefresh(selectId: created.LastOrDefault()?.Id);
        TriggerQueueAutoRunFromEnqueue();
        BuildingsInfoTextBlock.Text = $"Queued load + upgrade-to-max for {candidateRows.Count} slot(s).";
        AppendLog($"Upgrade-all-to-max: queued snapshot + {plannedRequests.Count} planned construction task(s), including {storageUpgrades.Count} storage prerequisite(s).");
    }

    internal void OnBuildingTemplatesClicked()
    {
        var status = ResolveSelectedVillageBuildingStatus();
        if (status is null || status.Buildings.Count == 0)
        {
            BuildingsInfoTextBlock.Text = "Load buildings for the selected village first.";
            return;
        }

        var window = new BuildingTemplatesWindow(
            _projectRoot,
            status,
            ResolveServerSpeed(),
            ResolveMainBuildingLevel())
        {
            Owner = this,
        };

        if (window.ShowDialog() != true || window.QueuePlan is null)
        {
            return;
        }

        QueueBuildingTemplatePlan(window.QueuePlan);
    }

    // Static reference image only — no village state needed, so unlike the templates window this
    // one opens regardless of whether buildings have been loaded yet.
    internal void OnShowBuildingSlotsClicked()
    {
        new BuildingSlotsWindow { Owner = this }.ShowDialog();
    }

    private void QueueBuildingTemplatePlan(BuildingTemplatePlanResult plan)
    {
        var prepared = plan.Actions.Select(action =>
        {
            var payload = new Dictionary<string, string>(action.Payload, StringComparer.OrdinalIgnoreCase);
            ApplySelectedVillageToPayload(payload);
            return (Action: action, Request: new QueueItemCreateRequest(action.TaskName, payload, 0, 3));
        }).ToList();

        IReadOnlyList<QueueItemCreateRequest> stagedResourceRequests = [];
        IReadOnlyList<StoragePreflightUpgrade> storageUpgrades = [];
        var bulkResourceActions = prepared
            .Where(item => string.Equals(
                item.Action.TaskName,
                "upgrade_all_resources_to_level",
                StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Request.Payload is not null
                && item.Request.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var rawTarget)
                && int.TryParse(rawTarget, out _))
            .ToList();
        if (bulkResourceActions.Count > 0)
        {
            var targetLevel = bulkResourceActions.Max(item =>
                int.Parse(item.Request.Payload![BotOptionPayloadKeys.ResourceUpgradeTargetLevel]));
            var parentPayload = bulkResourceActions
                .OrderByDescending(item => int.Parse(item.Request.Payload![BotOptionPayloadKeys.ResourceUpgradeTargetLevel]))
                .First()
                .Request.Payload!;
            if (!TryPrepareUpgradeAllStoragePreflight(
                    targetLevel,
                    parentPayload,
                    out stagedResourceRequests,
                    out storageUpgrades))
            {
                BuildingsInfoTextBlock.Text = "Building template cancelled by storage capacity preflight.";
                return;
            }

        }

        var finalRequests = new List<QueueItemCreateRequest>();
        var stagedResourcesInserted = false;
        foreach (var item in prepared)
        {
            if (string.Equals(item.Action.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
            {
                if (!stagedResourcesInserted)
                {
                    finalRequests.AddRange(stagedResourceRequests);
                    stagedResourcesInserted = true;
                }
                continue;
            }

            finalRequests.Add(item.Request);
        }

        if (!TryPrepareConstructionStoragePreflight(
                finalRequests,
                out var fullyPlannedRequests,
                out var additionalStorageUpgrades))
        {
            BuildingsInfoTextBlock.Text = "Building template cancelled by construction storage preflight.";
            return;
        }

        // Re-stamp every final row after both planners have expanded the template. This guarantees that
        // constructs, upgrades, resource stages, and auto-added storage dependencies all retain the exact
        // selected village coordinate key even when multiple villages share the same display name.
        finalRequests = fullyPlannedRequests.Select(request =>
        {
            var payload = request.Payload is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(request.Payload, StringComparer.OrdinalIgnoreCase);
            ApplySelectedVillageToPayload(payload);
            return request with { Payload = payload };
        }).ToList();
        storageUpgrades = storageUpgrades.Concat(additionalStorageUpgrades).ToList();

        IReadOnlyList<QueueItem> created;
        try
        {
            created = _botService.EnqueueBatch(finalRequests);
        }
        catch (Exception ex)
        {
            BuildingsInfoTextBlock.Text = $"Could not queue template: {ex.Message}";
            AppendLog($"[building-template] atomic queue insert failed; no template rows were added: {ex.Message}");
            return;
        }

        ApplyStoragePreflightPendingState(storageUpgrades);

        foreach (var item in prepared)
        {
            var action = item.Action;
            if (string.Equals(action.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && action.Gid is int constructGid)
            {
                SetPendingBuildingConstruct(action.SlotId, action.Payload.GetValueOrDefault(BotOptionPayloadKeys.BuildingConstructName, action.DisplayName), constructGid);
            }
            else if (string.Equals(action.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                && action.TargetLevel is int targetLevel
                && action.SlotId > 0)
            {
                SetPendingBuildingUpgrade(action.SlotId, targetLevel);
            }
        }

        RequestQueueUiRefresh(selectId: created.LastOrDefault()?.Id);
        TriggerQueueAutoRunFromEnqueue();
        var warningSuffix = plan.Warnings.Count > 0
            ? $" Warnings: {string.Join(" ", plan.Warnings.Take(2))}"
            : string.Empty;
        var storageSuffix = storageUpgrades.Count > 0
            ? $" Added {storageUpgrades.Count} storage prerequisite(s)."
            : string.Empty;
        BuildingsInfoTextBlock.Text = $"Queued building template: {created.Count} item(s).{storageSuffix}{warningSuffix}";
        AppendLog(
            $"Building template queued atomically: {created.Count} item(s), "
            + $"villageKey='{GetSelectedVillageKey() ?? "-"}'.{storageSuffix}{warningSuffix}");
    }

    internal void BuildingSlotCircleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSelectedVillageBuildingStatus() is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings for the selected village first.";
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

    private static bool IsRallyPointSlot(int slotId) => BuildingsViewModel.IsRallyPointSlot(slotId);

    private static bool IsRallyPointGid(int gid) => BuildingsViewModel.IsRallyPointGid(gid);

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
        ApplySelectedVillageToPayload(payload);
        if (!TryPrepareConstructionStoragePreflight(
                [new QueueItemCreateRequest("upgrade_building_to_level", payload, 0, 3)],
                out var plannedRequests,
                out var storageUpgrades))
        {
            return false;
        }

        var created = _botService.EnqueueBatch(plannedRequests);
        _buildingLastQueuedTargetBySlot[slotId] = (targetLevel, now);
        ApplyStoragePreflightPendingState(storageUpgrades);
        SetPendingBuildingUpgrade(slotId, targetLevel);
        RequestQueueUiRefresh(selectId: created.LastOrDefault()?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        UpgradeTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued {row.UpgradeName} in slot {slotId} to level {targetLevel}.";
        var storageSuffix = storageUpgrades.Count > 0
            ? $" Added {storageUpgrades.Count} storage prerequisite(s)."
            : string.Empty;
        AppendLog($"Queued single building upgrade: slot {slotId} -> level {targetLevel}.{storageSuffix}");
        return true;
    }

    private void ShowConstructChoicesForSlot(int slotId)
    {
        var status = ResolveSelectedVillageBuildingStatus();
        if (status is null || status.Buildings.Count == 0)
        {
            BuildingsInfoTextBlock.Text = "Load buildings for the selected village first.";
            return;
        }

        var options = GetClassifiedConstructOptionsForSlot(slotId, status);
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
        TryQueueConstructBuilding(slotId, selected, targetLevel);
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

    private IReadOnlyList<BuildingCatalogOption> GetClassifiedConstructOptionsForSlot(int slotId, VillageStatus sourceStatus)
    {
        var status = BuildProjectedBuildingStatus(sourceStatus);
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

            var conflictingResidenceFamilyGid = FindResidenceFamilyConflictGid(status.Buildings, entry.Gid);
            if (conflictingResidenceFamilyGid is int conflictGid)
            {
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{BuildingCatalogService.NameForGid(conflictGid)} already exists or is queued in this village";
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

            if (CanQueueConstructBuilding(slotId, option, sourceStatus, out var reason))
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
        var status = ResolveSelectedVillageBuildingStatus();
        if (status is null || status.Buildings.Count == 0)
        {
            reason = "Load buildings for the selected village first.";
            return false;
        }

        return CanQueueConstructBuilding(slotId, selectedBuilding, status, out reason);
    }

    private bool CanQueueConstructBuilding(int slotId, BuildingCatalogOption selectedBuilding, VillageStatus sourceStatus, out string reason)
    {
        reason = string.Empty;
        var projectedStatus = BuildProjectedBuildingStatus(sourceStatus);
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

        if (FindResidenceFamilyConflictGid(projectedStatus.Buildings, selectedBuilding.Gid) is int conflictingResidenceFamilyGid)
        {
            reason = $"{selectedBuilding.Name} conflicts with {BuildingCatalogService.NameForGid(conflictingResidenceFamilyGid)} already in this village.";
            return false;
        }

        if (BuildingCatalogService.DuplicateRequiredExistingLevelFor(selectedBuilding.Gid) is int duplicateRequiredLevel)
        {
            if (sameGidAlreadyPresent)
            {
                var currentHighest = existingSameGidLevels.DefaultIfEmpty(0).Max();
                if (currentHighest < duplicateRequiredLevel)
                {
                    reason = $"{selectedBuilding.Name} can only be duplicated after an existing one reaches level {duplicateRequiredLevel}.";
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

    private static int? FindResidenceFamilyConflictGid(IEnumerable<Building> buildings, int targetGid)
    {
        var conflictGids = BuildingCatalogService.ResidenceFamilyConflictGidsFor(targetGid);
        if (conflictGids.Count == 0)
        {
            return null;
        }

        foreach (var building in buildings)
        {
            var gid = building.Gid ?? BuildingCatalogService.GidForName(building.Name);
            if (gid is int value && conflictGids.Contains(value))
            {
                return value;
            }
        }

        return null;
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
        => BuildingsViewModel.IsUnbuiltFixedSpecialBuilding(slotId, building);

    private bool TryQueueConstructBuilding(
        int slotId,
        BuildingCatalogOption selectedBuilding,
        int selectedTargetLevel)
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

        var requests = new List<QueueItemCreateRequest>();
        var constructPayload = new BuildingConstructPayload(slotId, selectedBuilding.Gid, selectedBuilding.Name).ToDictionary();
        ApplySelectedVillageToPayload(constructPayload);
        requests.Add(new QueueItemCreateRequest("construct_building", constructPayload, 0, 3));
        var targetLevel = selectedTargetLevel == 0
            ? selectedBuilding.MaxLevel
            : Math.Clamp(selectedTargetLevel, 1, selectedBuilding.MaxLevel);
        if (targetLevel > 1)
        {
            var upgradePayload = new BuildingUpgradePayload(slotId, targetLevel, selectedBuilding.Name).ToDictionary();
            ApplySelectedVillageToPayload(upgradePayload);
            requests.Add(new QueueItemCreateRequest(
                selectedTargetLevel == 0 ? "upgrade_building_to_max" : "upgrade_building_to_level",
                upgradePayload,
                0,
                3));
        }

        if (!TryPrepareConstructionStoragePreflight(requests, out var plannedRequests, out var storageUpgrades))
        {
            return false;
        }

        var created = _botService.EnqueueBatch(plannedRequests);
        _buildingLastQueuedConstructBySlot[slotId] = (selectedBuilding.Name, selectedBuilding.Gid, now);
        ApplyStoragePreflightPendingState(storageUpgrades);
        SetPendingBuildingConstruct(slotId, selectedBuilding.Name, selectedBuilding.Gid);
        if (targetLevel > 1)
        {
            SetPendingBuildingUpgrade(slotId, targetLevel);
        }
        RequestQueueUiRefresh(selectId: created.LastOrDefault()?.Id);
        TriggerQueueAutoRunFromEnqueue();
        ConstructSlotTextBox.Text = slotId.ToString();
        ConstructBuildingComboBox.SelectedItem = _buildingCatalogOptions.FirstOrDefault(item => item.Gid == selectedBuilding.Gid);
        var targetText = selectedTargetLevel == 0
            ? " and upgrade to max"
            : targetLevel > 1 ? $" and upgrade to level {targetLevel}" : string.Empty;
        var storageSuffix = storageUpgrades.Count > 0
            ? $" Added {storageUpgrades.Count} storage prerequisite(s)."
            : string.Empty;
        BuildingsInfoTextBlock.Text = $"Queued construct{targetText}: {selectedBuilding.Name} in slot {slotId}.";
        AppendLog($"Queued building construct: slot {slotId} -> {selectedBuilding.Name}{targetText}.{storageSuffix}");
        return true;
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
        var existingTarget = GetQueuedBuildingTargetsBySlot(
                GetActiveQueueItems().Where(IsQueueItemForSelectedVillageOrGlobal).ToList())
            .GetValueOrDefault(slotId, 0);
        if (existingTarget >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{row.UpgradeName} already has a queued max upgrade.";
            return false;
        }

        var payload = new BuildingUpgradePayload(slotId, maxLevel, row.UpgradeName).ToDictionary();
        ApplySelectedVillageToPayload(payload);
        if (!TryPrepareConstructionStoragePreflight(
                [new QueueItemCreateRequest("upgrade_building_to_max", payload, 0, 3)],
                out var plannedRequests,
                out var storageUpgrades))
        {
            return false;
        }

        var created = _botService.EnqueueBatch(plannedRequests);
        ApplyStoragePreflightPendingState(storageUpgrades);
        SetPendingBuildingUpgrade(slotId, maxLevel);
        RequestQueueUiRefresh(selectId: created.LastOrDefault()?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        BuildingsInfoTextBlock.Text = $"Queued max-upgrade for slot {slotId}.";
        var storageSuffix = storageUpgrades.Count > 0
            ? $" Added {storageUpgrades.Count} storage prerequisite(s)."
            : string.Empty;
        AppendLog($"Queued building max-upgrade: slot {slotId}.{storageSuffix}");
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

        var snapshotVillageMatches = (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)?
            .Where(village => string.Equals(
                NormalizeVillageName(village.Name),
                NormalizeVillageName(snapshot.ActiveVillage),
                StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        if (snapshotVillageMatches.Count != 1)
        {
            AppendLog($"Buildings snapshot ignored for ambiguous or unknown village '{snapshot.ActiveVillage ?? "(unknown)"}'. A live village refresh is required.");
            return;
        }
        var snapshotVillage = snapshotVillageMatches[0];

        var status = new VillageStatus(
            ActiveVillage: snapshot.ActiveVillage ?? string.Empty,
            Villages: [new Village(
                snapshotVillage.Name,
                snapshotVillage.Url,
                snapshotVillage.IsCapital,
                snapshotVillage.CoordX,
                snapshotVillage.CoordY,
                snapshotVillage.Population,
                snapshotVillage.CropFields,
                snapshotVillage.Tribe)],
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
            IsCapital: snapshot.IsCapital,
            WarehouseCapacity: snapshot.WarehouseCapacity,
            GranaryCapacity: snapshot.GranaryCapacity,
            ActiveVillageCoordX: snapshotVillage.CoordX,
            ActiveVillageCoordY: snapshotVillage.CoordY);

        await Dispatcher.InvokeAsync(() =>
        {
            var uiStatus = MergeBuildingStatusForUi(status);
            CacheVillageStatus(uiStatus, snapshotVillage.Name);
            if (!IsStatusForSelectedVillage(uiStatus))
            {
                AppendLog($"Buildings snapshot cached for '{uiStatus.ActiveVillage}', but another village is selected.");
                return;
            }

            _lastBuildingStatus = uiStatus;
            ApplyTroopsAvailabilityFromVillageStatus(uiStatus);
            PopulateBuildingsTab(uiStatus);
            BuildingsInfoTextBlock.Text = $"Loaded {uiStatus.Buildings.Count} building slots from queue snapshot.";
        });
    }

    private sealed record BuildingSnapshotDto(
        string? Account,
        string? ActiveVillage,
        string? Tribe,
        bool? IsCapital,
        long? WarehouseCapacity,
        long? GranaryCapacity,
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
            var (occupied, slotName, slotLevel, slotGid) =
                BuildingsViewModel.ResolveSlotIdentity(slotId, building, status.Tribe);

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

            var isWallSlot = slotId == 40;
            var isRallyPointSlot = IsRallyPointSlot(slotId);

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
        => BuildingsViewModel.IsEmptyBuilding(building);

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
        => MissingRequirements(status, requirements, IsQueueItemForSelectedVillageOrGlobal, includeUiResourceRows: true);

    // Per-village requirement check. `queueFilter` scopes which queued items count toward satisfying a
    // requirement (selected-village-or-global for the UI, same-village-or-global for the cascade so another
    // village's queue can't falsely satisfy — or block — this village). `includeUiResourceRows` adds the
    // live Resources-tab levels, which only reflect the selected village, so it is off for other villages
    // (their resource-field levels come from the cached status instead).
    private List<BuildingRequirementEntry> MissingRequirements(
        VillageStatus status,
        IReadOnlyList<BuildingRequirementEntry> requirements,
        Func<QueueItem, bool> queueFilter,
        bool includeUiResourceRows)
    {
        var villageItems = GetActiveQueueItems().Where(queueFilter).ToList();
        var projectedStatus = BuildProjectedBuildingStatus(status, villageItems);
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
            var fromUiResourceRows = includeUiResourceRows ? MaxLevelInUiResourceRows(requirement.Name) : 0;
            // A queued (not yet built) construct of the required building counts as level 1: constructing
            // a building yields level 1 in-game, so queuing a prerequisite building should already unlock
            // the dependent one. Construct+upgrade chains are covered by projectedStatus above (the upgrade
            // projects the target level onto the constructed slot); this only adds the lone-construct case.
            var fromQueuedConstructs = QueuedConstructProvidesRequirement(requirement.Name, villageItems) ? 1 : 0;
            // A queued (pending) resource-field upgrade to level N satisfies a resource-field prerequisite
            // (e.g. Iron Mine 10 for an Iron Foundry) before it finishes, matching how queued building
            // upgrades/constructs already unlock dependent buildings.
            var fromQueuedResourceUpgrades = MaxQueuedResourceUpgradeLevel(requirement.Name, villageItems);
            // A building currently under construction (browser-confirmed active queue) to level N satisfies a
            // prerequisite before it finishes — e.g. the user manually started Academy 15, so Hospital (which
            // requires Academy 15) can be queued now. The worker still defers the dependent build until the
            // prerequisite actually completes. Mirrors how queued program constructs/upgrades above unlock
            // dependent buildings, but covers in-progress builds the program did not queue itself.
            var fromActiveConstructions = MaxActiveConstructionLevel(status, requirement.Name);
            var level = Math.Max(
                Math.Max(fromBuildings, fromResourceFields),
                Math.Max(
                    Math.Max(fromUiResourceRows, fromQueuedConstructs),
                    Math.Max(fromQueuedResourceUpgrades, fromActiveConstructions)));
            if (level < requirement.Level)
            {
                missing.Add(requirement);
            }
        }

        return missing;
    }

    private static bool QueuedConstructProvidesRequirement(string requirementName, IReadOnlyList<QueueItem> villageItems)
    {
        if (string.IsNullOrWhiteSpace(requirementName))
        {
            return false;
        }

        return villageItems
            .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .Any(item => TryReadBuildingConstructPayload(item.Payload, out _, out _, out var name)
                && !string.IsNullOrWhiteSpace(name)
                && name.Contains(requirementName, StringComparison.OrdinalIgnoreCase));
    }

    // How recently an in-progress build must have been read to be trusted as satisfying a prerequisite.
    // A build the user cancels in the browser lingers in the cached snapshot until the next scan; bounding
    // reliance to a fresh read keeps a stale (possibly cancelled) one from unlocking dependents indefinitely.
    private static readonly TimeSpan ActiveConstructionRequirementFreshness = TimeSpan.FromMinutes(30);

    // Highest target level of a building currently under construction (browser-confirmed ActiveConstructions)
    // whose name matches the prerequisite. Counts in-progress builds the program did not queue itself (e.g. a
    // user-started Academy upgrade), so a dependent building can be queued ahead and built once the
    // prerequisite finishes. Returns 0 when no active construction matches.
    private static int MaxActiveConstructionLevel(VillageStatus status, string requirementName)
    {
        if (string.IsNullOrWhiteSpace(requirementName))
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        return ConstructionQueueState.ResolveCurrentActiveConstructions(status)
            .Where(item => item.Level is not null
                && !string.IsNullOrWhiteSpace(item.Name)
                && item.Name.Contains(requirementName, StringComparison.OrdinalIgnoreCase)
                // Only a freshly read build is trusted (see ActiveConstructionRequirementFreshness). Items
                // without a read timestamp keep the prior behavior rather than being dropped.
                && (item.Finish is null || now - item.Finish.ReadAtUtc <= ActiveConstructionRequirementFreshness))
            .Select(item => item.Level!.Value)
            .DefaultIfEmpty(0)
            .Max();
    }

    // Highest target level of a queued (pending) resource-field upgrade for the prerequisite's resource
    // category (Woodcutter/Clay Pit/Iron Mine/Cropland). Returns 0 for non-resource requirements.
    // upgrade_all_resources_to_level raises every field, so its target counts for any resource category.
    private static int MaxQueuedResourceUpgradeLevel(string requirementName, IReadOnlyList<QueueItem> villageItems)
    {
        var reqCategory = ResourceCategory(requirementName);
        if (reqCategory is null)
        {
            return 0;
        }

        var best = 0;
        foreach (var item in villageItems)
        {
            var payload = item.Payload;
            int? target = null;
            if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            {
                var name = GetPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeName);
                if (string.Equals(ResourceCategory(name), reqCategory, StringComparison.Ordinal))
                {
                    target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                        ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
                }
            }
            else if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
            {
                target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                    ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
            }

            if (target is int value)
            {
                best = Math.Max(best, value);
            }
        }

        return best;
    }

    // Maps a resource field / requirement name to its resource category, or null when it is not a
    // resource field (e.g. Main Building, Academy).
    private static string? ResourceCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (name.Contains("Clay", StringComparison.OrdinalIgnoreCase)) return "clay";
        if (name.Contains("Iron", StringComparison.OrdinalIgnoreCase)) return "iron";
        if (name.Contains("Crop", StringComparison.OrdinalIgnoreCase)) return "crop";
        return null;
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
