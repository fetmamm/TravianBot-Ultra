using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed record StoragePreflightUpgrade(
    StorageCapacityKind Kind,
    int SlotId,
    int CurrentLevel,
    int TargetLevel,
    long RequiredCapacity,
    long ProjectedCapacity);

public sealed record StorageCapacityQueuePreflightResult(
    IReadOnlyList<StoragePreflightUpgrade> Upgrades,
    long RequiredWarehouseCapacity,
    long RequiredGranaryCapacity,
    long ProjectedWarehouseCapacity,
    long ProjectedGranaryCapacity,
    string? CannotPlanReason = null)
{
    public bool NeedsUpgrades => Upgrades.Count > 0;
}

public sealed record StorageCapacityQueuePreflightStage(
    int ResourceTargetLevel,
    IReadOnlyList<StoragePreflightUpgrade> StorageUpgradesBefore);

public sealed record StorageCapacityQueueStepwiseResult(
    IReadOnlyList<StorageCapacityQueuePreflightStage> Stages,
    string? CannotPlanReason = null)
{
    public IReadOnlyList<StoragePreflightUpgrade> Upgrades =>
        Stages.SelectMany(stage => stage.StorageUpgradesBefore).ToList();
}

public static class StorageCapacityQueuePreflightPlanner
{
    public static StorageCapacityQueuePreflightResult PlanUpgradeAllResources(
        VillageStatus status,
        IReadOnlyList<QueueItem> precedingQueueItems,
        int targetLevel)
    {
        var requiredWarehouse = 0L;
        var requiredGranary = 0L;
        foreach (var field in status.ResourceFields.Where(field => (field.Level ?? 0) < targetLevel))
        {
            var gid = BuildingCatalogService.GidForName(field.FieldType)
                ?? BuildingCatalogService.GidForName(field.Name);
            if (gid is not (>= 1 and <= 4))
            {
                continue;
            }

            for (var level = Math.Max(1, (field.Level ?? 0) + 1); level <= targetLevel; level++)
            {
                var cost = BuildingCatalogService.CostFor(gid.Value, level);
                if (cost is null)
                {
                    continue;
                }

                requiredWarehouse = Math.Max(requiredWarehouse, Math.Max(cost.Wood, Math.Max(cost.Clay, cost.Iron)));
                requiredGranary = Math.Max(requiredGranary, cost.Crop);
            }
        }

        var projection = CreateStorageProjection(status, precedingQueueItems);
        var storageBySlot = projection.StorageBySlot;
        var projectedWarehouse = projection.WarehouseCapacity;
        var projectedGranary = projection.GranaryCapacity;

        var upgrades = new List<StoragePreflightUpgrade>();
        var failure = AddRequiredUpgrade(
            StorageCapacityKind.Warehouse,
            requiredWarehouse,
            ref projectedWarehouse,
            storageBySlot,
            upgrades);
        failure ??= AddRequiredUpgrade(
            StorageCapacityKind.Granary,
            requiredGranary,
            ref projectedGranary,
            storageBySlot,
            upgrades);

        return new StorageCapacityQueuePreflightResult(
            upgrades,
            requiredWarehouse,
            requiredGranary,
            projectedWarehouse,
            projectedGranary,
            failure);
    }

    public static StorageCapacityQueueStepwiseResult PlanUpgradeAllResourcesStepwise(
        VillageStatus status,
        IReadOnlyList<QueueItem> precedingQueueItems,
        int targetLevel)
    {
        var projectedFieldLevels = status.ResourceFields
            .Where(field => field.SlotId is >= 1 and <= 18)
            .ToDictionary(field => field.SlotId!.Value, field => field.Level ?? 0);
        foreach (var item in precedingQueueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
        {
            if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var rawBulkTarget)
                && int.TryParse(rawBulkTarget, out var bulkTarget))
            {
                foreach (var slot in projectedFieldLevels.Keys.ToList())
                {
                    projectedFieldLevels[slot] = Math.Max(projectedFieldLevels[slot], bulkTarget);
                }
            }
            else if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                && ResourceUpgradePayload.TryFromDictionary(item.Payload, out var resourceUpgrade)
                && resourceUpgrade is not null
                && projectedFieldLevels.ContainsKey(resourceUpgrade.SlotId))
            {
                projectedFieldLevels[resourceUpgrade.SlotId] = Math.Max(
                    projectedFieldLevels[resourceUpgrade.SlotId],
                    resourceUpgrade.TargetLevel);
            }
        }

        var fields = status.ResourceFields
            .Where(field => field.SlotId is int slot && projectedFieldLevels.ContainsKey(slot))
            .Select(field => (Field: field, Level: projectedFieldLevels[field.SlotId!.Value]))
            .ToList();
        if (!fields.Any(field => field.Level < targetLevel))
        {
            return new StorageCapacityQueueStepwiseResult([]);
        }

        var projection = CreateStorageProjection(status, precedingQueueItems);
        var projectedWarehouse = projection.WarehouseCapacity;
        var projectedGranary = projection.GranaryCapacity;
        var pendingUpgrades = new List<StoragePreflightUpgrade>();
        var stages = new List<StorageCapacityQueuePreflightStage>();
        var lastStageTarget = fields.Min(field => field.Level);

        for (var level = 1; level <= targetLevel; level++)
        {
            var requiredWarehouse = 0L;
            var requiredGranary = 0L;
            foreach (var (field, currentLevel) in fields.Where(field => field.Level < level))
            {
                var gid = BuildingCatalogService.GidForName(field.FieldType)
                    ?? BuildingCatalogService.GidForName(field.Name);
                var cost = gid is >= 1 and <= 4 ? BuildingCatalogService.CostFor(gid.Value, level) : null;
                if (cost is null)
                {
                    continue;
                }

                requiredWarehouse = Math.Max(requiredWarehouse, Math.Max(cost.Wood, Math.Max(cost.Clay, cost.Iron)));
                requiredGranary = Math.Max(requiredGranary, cost.Crop);
            }

            if (requiredWarehouse <= projectedWarehouse && requiredGranary <= projectedGranary)
            {
                continue;
            }

            var safeTarget = level - 1;
            if (safeTarget > lastStageTarget && fields.Any(field => field.Level < safeTarget))
            {
                stages.Add(new StorageCapacityQueuePreflightStage(safeTarget, pendingUpgrades.ToList()));
                pendingUpgrades.Clear();
                lastStageTarget = safeTarget;
            }

            var failure = AddRequiredUpgrade(
                StorageCapacityKind.Warehouse,
                requiredWarehouse,
                ref projectedWarehouse,
                projection.StorageBySlot,
                pendingUpgrades);
            failure ??= AddRequiredUpgrade(
                StorageCapacityKind.Granary,
                requiredGranary,
                ref projectedGranary,
                projection.StorageBySlot,
                pendingUpgrades);
            if (failure is not null)
            {
                return new StorageCapacityQueueStepwiseResult(stages, failure);
            }
        }

        stages.Add(new StorageCapacityQueuePreflightStage(targetLevel, pendingUpgrades.ToList()));
        return new StorageCapacityQueueStepwiseResult(stages);
    }

    private static StorageProjection CreateStorageProjection(
        VillageStatus status,
        IReadOnlyList<QueueItem> precedingQueueItems)
    {
        var storageBySlot = status.Buildings
            .Where(building => building.SlotId is >= 19 and <= 38)
            .Where(building => building.Gid is 10 or 11)
            .ToDictionary(building => building.SlotId!.Value, building => building);
        var projectedWarehouse = status.WarehouseCapacity ?? 0;
        var projectedGranary = status.GranaryCapacity ?? 0;

        foreach (var item in precedingQueueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                && construct is not null
                && construct.Gid is 10 or 11
                && !storageBySlot.ContainsKey(construct.SlotId))
            {
                storageBySlot[construct.SlotId] = new Building(
                    construct.SlotId,
                    BuildingCatalogService.NameForGid(construct.Gid),
                    1,
                    null,
                    construct.Gid);
                if (construct.Gid == 10)
                {
                    projectedWarehouse += StorageCapacityDependencyPlanner.CapacityAtLevel(1);
                }
                else
                {
                    projectedGranary += StorageCapacityDependencyPlanner.CapacityAtLevel(1);
                }

                continue;
            }

            if (!string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || !BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
                || upgrade is null
                || !upgrade.TargetLevel.HasValue
                || !storageBySlot.TryGetValue(upgrade.SlotId, out var building)
                || building.Gid is not (10 or 11))
            {
                continue;
            }

            var currentLevel = building.Level ?? 0;
            var projectedLevel = Math.Max(currentLevel, upgrade.TargetLevel.Value);
            var addedCapacity = StorageCapacityDependencyPlanner.CapacityAtLevel(projectedLevel)
                - StorageCapacityDependencyPlanner.CapacityAtLevel(currentLevel);
            if (building.Gid == 10)
            {
                projectedWarehouse += addedCapacity;
            }
            else
            {
                projectedGranary += addedCapacity;
            }

            storageBySlot[upgrade.SlotId] = building with { Level = projectedLevel };
        }

        return new StorageProjection(storageBySlot, projectedWarehouse, projectedGranary);
    }

    private static string? AddRequiredUpgrade(
        StorageCapacityKind kind,
        long requiredCapacity,
        ref long projectedCapacity,
        IDictionary<int, Building> storageBySlot,
        ICollection<StoragePreflightUpgrade> upgrades)
    {
        if (requiredCapacity <= projectedCapacity)
        {
            return null;
        }

        var gid = kind == StorageCapacityKind.Warehouse ? 10 : 11;
        var candidate = storageBySlot.Values
            .Where(building => building.Gid == gid && building.Level is >= 1 and < 20)
            .OrderByDescending(building => building.Level)
            .FirstOrDefault();
        if (candidate?.SlotId is not int slotId || candidate.Level is not int currentLevel)
        {
            return $"No upgradeable {BuildingCatalogService.NameForGid(gid)} is known in the village snapshot.";
        }

        var targetLevel = StorageCapacityDependencyPlanner.ResolveUpgradeTargetLevel(
            currentLevel,
            projectedCapacity,
            requiredCapacity,
            BuildingCatalogService.MaxLevelFor(gid));
        var addedCapacity = StorageCapacityDependencyPlanner.CapacityAtLevel(targetLevel)
            - StorageCapacityDependencyPlanner.CapacityAtLevel(currentLevel);
        upgrades.Add(new StoragePreflightUpgrade(
            kind,
            slotId,
            currentLevel,
            targetLevel,
            requiredCapacity,
            projectedCapacity));
        projectedCapacity += addedCapacity;
        storageBySlot[slotId] = candidate with { Level = targetLevel };
        return null;
    }

    private sealed record StorageProjection(
        Dictionary<int, Building> StorageBySlot,
        long WarehouseCapacity,
        long GranaryCapacity);
}
