using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

// Build-time and resource-cost estimates for queued construction tasks. Reads the (1x) buildings
// catalog and scales by the detected server speed. Estimates are best-effort: missing data only
// leaves the columns blank (and raises a one-time alarm), never blocks queue execution.
public partial class MainWindow
{
    private readonly HashSet<string> _estimateAlarmedKeys = new(StringComparer.OrdinalIgnoreCase);

    // Specific slot/level construction upgrades that can be auto-removed once their target level is
    // already reached. Aggregate resource tasks and constructs are left to self-clear at runtime.
    private static readonly HashSet<string> AutoPrunableCompletedTasks = new(StringComparer.OrdinalIgnoreCase)
    {
        "upgrade_building_to_level",
        "upgrade_building_to_max",
        "upgrade_resource_to_level",
    };

    // Removes Pending construction upgrades whose target level is already met — the user may have built
    // the level manually, or an earlier queued step already covers it, so the estimate resolves to
    // 0 cost / 0 time and the task can never build anything. Returns true when something was removed.
    // Estimates every item in queue order first so per-slot coverage is accumulated correctly.
    private bool PruneCompletedConstructionQueueItems(IReadOnlyList<QueueItem> ordered)
    {
        var serverSpeed = ResolveServerSpeed();
        var mainBuildingLevel = ResolveMainBuildingLevel();
        var coverage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var removedAny = false;
        foreach (var item in ordered)
        {
            var activeCoveredLevel = ConstructionQueueCoverage.ResolveActiveCoveredLevel(
                item,
                ResolveBuildingStatusForQueueItem(item));
            if (activeCoveredLevel is int coveredLevel)
            {
                if (_botService.RemoveQueueItem(item.Id))
                {
                    removedAny = true;
                    AppendLog($"[queue] removed covered construction '{BuildQueueDisplayName(item)}' — " +
                        $"Travian queue already covers slot through level {coveredLevel}.");
                }
                continue;
            }

            var estimate = EstimateForQueueItem(item, serverSpeed, mainBuildingLevel, coverage);
            if (item.Status != QueueStatus.Pending
                || !AutoPrunableCompletedTasks.Contains(item.TaskName ?? string.Empty)
                || !estimate.HasData
                || estimate.Seconds > 0)
            {
                continue;
            }

            if (_botService.RemoveQueueItem(item.Id))
            {
                removedAny = true;
                AppendLog($"[queue] removed already-completed construction '{BuildQueueDisplayName(item)}' — target level already reached, nothing left to build.");
            }
        }

        return removedAny;
    }

    // Main Building level of the currently loaded village (gid 15), used to discount build time.
    // Defaults to 1 (no discount) until the village's buildings have been scanned.
    private int ResolveMainBuildingLevel()
    {
        var level = _buildingRows
            .Where(r => r.Gid == 15 || string.Equals(r.Name, "Main Building", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Level)
            .FirstOrDefault();
        return level is int value && value > 0 ? value : 1;
    }

    private QueueItemEstimate EstimateForQueueItem(QueueItem item, double serverSpeed, int loadedVillageMainBuildingLevel, Dictionary<string, int> queuedCoverage)
    {
        if (item is null || BuildingCatalogService.CatalogLoadError is not null)
        {
            return QueueItemEstimate.None;
        }

        var payload = item.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taskName = item.TaskName ?? string.Empty;
        var villageLoaded = IsQueueItemForLoadedVillage(item);
        // The Main Building level is only known for the loaded village; other villages fall back to 1.
        var mainBuildingLevel = villageLoaded ? loadedVillageMainBuildingLevel : 1;

        if (string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            var gid = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingConstructGid)
                ?? BuildingCatalogService.GidForName(GetPayloadValue(payload, BotOptionPayloadKeys.BuildingConstructName));
            return SumLevels(item, gid, 1, 1, serverSpeed, mainBuildingLevel);
        }

        if (string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase))
        {
            var slotId = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeSlotId);
            var name = GetPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeName);
            var target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeTargetLevel)
                ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
            if (target is null)
            {
                return QueueItemEstimate.None;
            }

            var (gid, currentLevel) = ResolveBuildingGidAndLevel(slotId, name, villageLoaded);
            return SumLevelsWithQueueCoverage(item, gid, currentLevel, target.Value, serverSpeed, mainBuildingLevel, slotId, name, queuedCoverage);
        }

        if (string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            var slotId = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeSlotId);
            var name = GetPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeName);
            var (gid, currentLevel) = ResolveBuildingGidAndLevel(slotId, name, villageLoaded);
            if (gid is null || !currentLevel.HasValue)
            {
                // Without the current level the range is undefined; leave blank without alarming.
                return QueueItemEstimate.None;
            }

            return SumLevelsWithQueueCoverage(item, gid, currentLevel, BuildingCatalogService.MaxLevelFor(gid.Value), serverSpeed, mainBuildingLevel, slotId, name, queuedCoverage);
        }

        if (string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
        {
            var slotId = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeSlotId);
            var name = GetPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeName)
                ?? (slotId.HasValue ? ResolveResourceName(slotId.Value) : null);
            var target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
            if (target is null)
            {
                return QueueItemEstimate.None;
            }

            var gid = BuildingCatalogService.GidForName(name);
            var currentLevel = villageLoaded && slotId.HasValue ? ResolveResourceLevel(slotId.Value) : null;
            return SumLevelsWithQueueCoverage(item, gid, currentLevel, target.Value, serverSpeed, mainBuildingLevel, slotId, name, queuedCoverage);
        }

        if (string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
        {
            var target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
            if (target is null || !villageLoaded)
            {
                return QueueItemEstimate.None;
            }

            if (!HasCompleteResourceEstimateRows(_resourcesViewModel.AllFields))
            {
                return QueueItemEstimate.None;
            }

            if (!ResourceFieldUpgradeEstimator.TryEstimate(
                    _resourcesViewModel.AllFields,
                    target.Value,
                    serverSpeed,
                    mainBuildingLevel,
                    out var estimate,
                    out var failureReason))
            {
                RaiseEstimateAlarm(item, failureReason ?? "resource field estimate failed");
                return QueueItemEstimate.None;
            }

            return new QueueItemEstimate(
                true,
                estimate.Seconds,
                estimate.Wood,
                estimate.Clay,
                estimate.Iron,
                estimate.Crop);
        }

        // Everything else (farming, hero, transfers, demolish, ...) is not estimable.
        return QueueItemEstimate.None;
    }

    // Sums catalog cost/time across the inclusive level range [fromLevel, toLevel]. Returns None and
    // raises a one-time alarm when the gid is unknown or catalog data is missing for a level.
    private QueueItemEstimate SumLevels(QueueItem item, int? gid, int fromLevel, int toLevel, double serverSpeed, int mainBuildingLevel)
    {
        if (gid is null)
        {
            RaiseEstimateAlarm(item, "building could not be matched to the catalog");
            return QueueItemEstimate.None;
        }

        if (toLevel < fromLevel)
        {
            // Already at/above target: nothing left to build.
            return new QueueItemEstimate(true, 0, 0, 0, 0, 0);
        }

        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        for (var level = fromLevel; level <= toLevel; level++)
        {
            var stats = BuildingCatalogService.CostFor(gid.Value, level);
            if (stats is null)
            {
                RaiseEstimateAlarm(item, $"missing catalog data for gid {gid.Value} level {level}");
                return QueueItemEstimate.None;
            }

            seconds += BuildingCatalogService.BuildSecondsFor(gid.Value, level, serverSpeed, mainBuildingLevel);
            wood += stats.Wood;
            clay += stats.Clay;
            iron += stats.Iron;
            crop += stats.Crop;
        }

        return new QueueItemEstimate(true, seconds, wood, clay, iron, crop);
    }

    // Coverage-aware variant of SumLevels for progressive upgrades of the same building/field. When the
    // queue holds several upgrades of one slot (e.g. Rally Point to level 2, 3, ..., 10), each item only
    // performs the single step from the previous queued target — not the whole path from the live current
    // level. queuedCoverage carries the highest target already covered by earlier queued items so the row
    // (and the Totals row) reflect just this item's own work instead of re-counting the shared lower
    // levels. Items not yet queued for execution (history) do not participate.
    private QueueItemEstimate SumLevelsWithQueueCoverage(
        QueueItem item,
        int? gid,
        int? currentLevel,
        int target,
        double serverSpeed,
        int mainBuildingLevel,
        int? slotId,
        string? buildingName,
        Dictionary<string, int> queuedCoverage)
    {
        var coverageKey = ResolveQueueCoverageKey(item, gid, slotId, buildingName);

        int? floorLevel = currentLevel;
        if (coverageKey is not null && queuedCoverage.TryGetValue(coverageKey, out var covered))
        {
            floorLevel = floorLevel.HasValue ? Math.Max(floorLevel.Value, covered) : covered;
        }

        // Known floor (live level or earlier queued target) -> only the next step(s); unknown floor falls
        // back to the existing behavior of estimating just the target level.
        var fromLevel = floorLevel.HasValue ? floorLevel.Value + 1 : target;
        var estimate = SumLevels(item, gid, fromLevel, target, serverSpeed, mainBuildingLevel);

        if (coverageKey is not null)
        {
            queuedCoverage[coverageKey] = Math.Max(queuedCoverage.GetValueOrDefault(coverageKey), target);
        }

        return estimate;
    }

    // Stable per-building coverage key, scoped to the village so the same slot in two villages never
    // shares coverage. Only Pending/Running/Paused items accumulate coverage — a Succeeded upgrade is
    // already reflected in the live current level, so counting it would double-subtract.
    private string? ResolveQueueCoverageKey(QueueItem item, int? gid, int? slotId, string? buildingName)
    {
        if (item.Status is not (QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused))
        {
            return null;
        }

        var village = NormalizeVillageName(GetQueueItemVillageName(item)) ?? string.Empty;
        if (slotId.HasValue)
        {
            return $"{village}|slot:{slotId.Value}";
        }

        return gid.HasValue
            ? $"{village}|gid:{gid.Value}|{(buildingName ?? string.Empty).ToLowerInvariant()}"
            : null;
    }

    // Resolves gid and current level for a building. Current level is only available when the item
    // targets the currently loaded village (its slots live in _buildingRows). A slot that is only queued
    // for construction has no built level yet but a separate construct item builds it to level 1, so the
    // upgrade is estimated as starting from level 1 (avoiding double-counting that first level).
    private (int? Gid, int? CurrentLevel) ResolveBuildingGidAndLevel(int? slotId, string? name, bool villageLoaded)
    {
        var gid = BuildingCatalogService.GidForName(name);
        int? currentLevel = null;
        if (villageLoaded && slotId.HasValue)
        {
            var row = _buildingRows.FirstOrDefault(r => r.SlotId == slotId.Value);
            if (row is not null)
            {
                gid ??= row.UpgradeGid;
                currentLevel = row.Level ?? (row.HasPendingConstruct ? 1 : null);
            }
        }

        return (gid, currentLevel);
    }

    private int? ResolveResourceLevel(int slotId)
        => _resourcesViewModel.AllFields.FirstOrDefault(r => r.SlotId == slotId)?.Level;

    private static bool HasCompleteResourceEstimateRows(IReadOnlyList<ResourceFieldRow> rows)
    {
        var bySlot = rows
            .Where(row => row.SlotId is >= 1 and <= 18)
            .GroupBy(row => row.SlotId)
            .ToList();
        if (bySlot.Count != 18)
        {
            return false;
        }

        return bySlot.All(group =>
        {
            var row = group.First();
            return row.Level is >= 0
                && (BuildingCatalogService.GidForName(row.Name) is not null
                    || BuildingCatalogService.GidForName(row.FieldType) is not null);
        });
    }

    private bool IsQueueItemForLoadedVillage(QueueItem item)
    {
        // Key-based: a renamed village keeps its coordinate key, so its buildings/resource rows (loaded
        // for the selected village) are correctly matched to its queue items for cost/time estimates.
        var itemKey = GetQueueItemVillageKey(item);
        if (string.IsNullOrEmpty(itemKey))
        {
            return true;
        }

        var selectedKey = GetSelectedVillageKey();
        return !string.IsNullOrEmpty(selectedKey)
            && string.Equals(itemKey, selectedKey, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseEstimateAlarm(QueueItem item, string reason)
    {
        var key = $"{item.Id}:{reason}";
        if (_estimateAlarmedKeys.Add(key))
        {
            AppendLog($"ALARM: could not estimate build cost/time for task '{item.TaskName}': {reason}.");
        }
    }

    // Cost + time of the next level for a clicked building slot, shown in the slot popup. Returns null
    // when the slot is empty/at max or has no catalog data so the popup hides the estimate section.
    private BuildingNextLevelEstimate? BuildNextLevelEstimate(BuildingSlotRow slot)
    {
        if (!slot.CanQueueUpgrade || slot.IsMaxLevel || slot.UpgradeGid is not int gid)
        {
            return null;
        }

        var nextLevel = slot.UpgradeBaseLevel + 1;
        var stats = BuildingCatalogService.CostFor(gid, nextLevel);
        if (stats is null)
        {
            return null;
        }

        var seconds = BuildingCatalogService.BuildSecondsFor(gid, nextLevel, ResolveServerSpeed(), ResolveMainBuildingLevel());
        return new BuildingNextLevelEstimate(
            nextLevel,
            seconds,
            FormatBuildDuration(seconds),
            FormatResourceAmount(stats.Wood),
            FormatResourceAmount(stats.Clay),
            FormatResourceAmount(stats.Iron),
            FormatResourceAmount(stats.Crop));
    }

    // Cumulative cost + time for upgrading a slot from its current/pending level up to targetLevel,
    // shown live in the "Upgrade to..." window.
    private BuildingNextLevelEstimate? BuildUpgradeRangeEstimate(BuildingSlotRow slot, int targetLevel)
    {
        return slot.UpgradeGid is int gid
            ? BuildRangeEstimate(gid, slot.UpgradeBaseLevel + 1, targetLevel)
            : null;
    }

    // Cumulative cost + time for constructing a new building from level 1 up to targetLevel, shown live
    // in the "Choose building" window.
    private BuildingNextLevelEstimate? BuildConstructRangeEstimate(int gid, int targetLevel)
    {
        return BuildRangeEstimate(gid, 1, targetLevel);
    }

    // Cumulative cost + time across the inclusive level range [fromLevel, toLevel] for the popups.
    // Returns null when the target is not above the base level or any level's catalog data is missing.
    private BuildingNextLevelEstimate? BuildRangeEstimate(int gid, int fromLevel, int toLevel)
    {
        if (toLevel < fromLevel)
        {
            return null;
        }

        var serverSpeed = ResolveServerSpeed();
        var mainBuildingLevel = ResolveMainBuildingLevel();
        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        for (var level = fromLevel; level <= toLevel; level++)
        {
            var stats = BuildingCatalogService.CostFor(gid, level);
            if (stats is null)
            {
                return null;
            }

            seconds += BuildingCatalogService.BuildSecondsFor(gid, level, serverSpeed, mainBuildingLevel);
            wood += stats.Wood;
            clay += stats.Clay;
            iron += stats.Iron;
            crop += stats.Crop;
        }

        return new BuildingNextLevelEstimate(
            toLevel,
            seconds,
            FormatBuildDuration(seconds),
            FormatResourceAmount(wood),
            FormatResourceAmount(clay),
            FormatResourceAmount(iron),
            FormatResourceAmount(crop));
    }

    // Compact build-time formatting: "1d 3h", "2h 15m", "5m 30s", "45s".
    private static string FormatBuildDuration(double totalSeconds)
        => QueueItemRowFactory.FormatBuildDuration(totalSeconds);

    private static string FormatResourceAmount(long amount)
        => QueueItemRowFactory.FormatResourceAmount(amount);

    // Sums the construction estimates of the rows currently shown (already filtered to the selected
    // village) and pushes them to the totals row below the queue grid. Called from RefreshQueueUi so
    // the totals update automatically on add/remove and village switch.
    private void UpdateQueueEstimateTotals(IReadOnlyList<QueueItemRow> rows)
    {
        if (QueueTotalWoodTextBlock is null)
        {
            return;
        }

        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        var counted = 0;
        foreach (var row in rows)
        {
            if (!row.HasEstimate)
            {
                continue;
            }

            counted++;
            seconds += row.EstimateSeconds;
            wood += row.EstimateWood;
            clay += row.EstimateClay;
            iron += row.EstimateIron;
            crop += row.EstimateCrop;
        }

        QueueTotalTimeTextBlock.Text = counted > 0 ? FormatBuildDuration(seconds) : "-";
        QueueTotalTimeConstructFasterTextBlock.Text = counted > 0
            ? FormatBuildDuration(seconds * 0.75)
            : "-";
        QueueTotalWoodTextBlock.Text = counted > 0 ? FormatResourceAmount(wood) : "-";
        QueueTotalClayTextBlock.Text = counted > 0 ? FormatResourceAmount(clay) : "-";
        QueueTotalIronTextBlock.Text = counted > 0 ? FormatResourceAmount(iron) : "-";
        QueueTotalCropTextBlock.Text = counted > 0 ? FormatResourceAmount(crop) : "-";
    }
}
