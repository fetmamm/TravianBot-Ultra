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
    long ProjectedCapacity)
{
    public bool RequiresConstruction => CurrentLevel == 0;
}

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

public sealed record ConstructionStoragePreflightResult(
    IReadOnlyList<QueueItemCreateRequest> Requests,
    IReadOnlyList<StoragePreflightUpgrade> Upgrades,
    string? CannotPlanReason = null);

public static class StorageCapacityQueuePreflightPlanner
{
    public static ConstructionStoragePreflightResult PlanConstructionRequestsStepwise(
        VillageStatus status,
        IReadOnlyList<QueueItem> precedingQueueItems,
        IReadOnlyList<QueueItemCreateRequest> requestedItems)
    {
        var projection = CreateStorageProjection(status, precedingQueueItems);
        var projectedWarehouse = projection.WarehouseCapacity;
        var projectedGranary = projection.GranaryCapacity;
        var buildingsBySlot = status.Buildings
            .Where(building => building.SlotId is int)
            .GroupBy(building => building.SlotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(building => building.Level ?? 0).First());
        var fieldsBySlot = status.ResourceFields
            .Where(field => field.SlotId is >= 1 and <= 18)
            .ToDictionary(field => field.SlotId!.Value, field => field);

        ApplyQueuedTargets(precedingQueueItems, buildingsBySlot, fieldsBySlot);

        var planned = new List<QueueItemCreateRequest>();
        var allStorageUpgrades = new List<StoragePreflightUpgrade>();
        foreach (var request in requestedItems)
        {
            var payload = request.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(request.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && BuildingConstructPayload.TryFromDictionary(payload, out var construct)
                && construct is not null)
            {
                projection.OccupiedBuildingSlots.Add(construct.SlotId);
                var failure = EnsureCapacityForCost(
                    BuildingCatalogService.CostFor(construct.Gid, 1),
                    construct.Gid is 10 or 11 ? construct.SlotId : null,
                    payload,
                    request,
                    projection,
                    buildingsBySlot,
                    ref projectedWarehouse,
                    ref projectedGranary,
                    planned,
                    allStorageUpgrades);
                if (failure is not null)
                {
                    return new ConstructionStoragePreflightResult(planned, allStorageUpgrades, failure);
                }

                planned.Add(CloneRequest(request));
                buildingsBySlot[construct.SlotId] = new Building(
                    construct.SlotId,
                    construct.Name ?? BuildingCatalogService.NameForGid(construct.Gid),
                    1,
                    null,
                    construct.Gid);
                ApplyStorageBuildingLevelChange(
                    construct.Gid,
                    0,
                    1,
                    ref projectedWarehouse,
                    ref projectedGranary);
                if (construct.Gid is 10 or 11)
                {
                    projection.StorageBySlot[construct.SlotId] = buildingsBySlot[construct.SlotId];
                }
                continue;
            }

            if ((string.Equals(request.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(request.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && BuildingUpgradePayload.TryFromDictionary(payload, out var buildingUpgrade)
                && buildingUpgrade is not null)
            {
                if (!buildingsBySlot.TryGetValue(buildingUpgrade.SlotId, out var building)
                    || (building.Gid ?? BuildingCatalogService.GidForName(buildingUpgrade.Name)) is not int gid)
                {
                    return new ConstructionStoragePreflightResult(
                        planned,
                        allStorageUpgrades,
                        $"Building slot {buildingUpgrade.SlotId} has no known building type in the village snapshot.");
                }

                var currentLevel = building.Level ?? 0;
                var targetLevel = buildingUpgrade.TargetLevel
                    ?? BuildingCatalogService.MaxLevelFor(gid);
                targetLevel = Math.Max(currentLevel, targetLevel);
                var stageStart = currentLevel;
                for (var level = currentLevel + 1; level <= targetLevel; level++)
                {
                    var plannedCountBefore = planned.Count;
                    var storageCountBefore = allStorageUpgrades.Count;
                    var failure = EnsureCapacityForCost(
                        BuildingCatalogService.CostFor(gid, level),
                        gid is 10 or 11 ? buildingUpgrade.SlotId : null,
                        payload,
                        request,
                        projection,
                        buildingsBySlot,
                        ref projectedWarehouse,
                        ref projectedGranary,
                        planned,
                        allStorageUpgrades);
                    if (failure is not null)
                    {
                        return new ConstructionStoragePreflightResult(planned, allStorageUpgrades, failure);
                    }

                    if (allStorageUpgrades.Count > storageCountBefore && level - 1 > stageStart)
                    {
                        InsertUpgradeBeforeTrailingStorageRequests(
                            planned,
                            plannedCountBefore,
                            CreateBuildingUpgradeRequest(request, buildingUpgrade, level - 1));
                        stageStart = level - 1;
                    }

                    ApplyStorageBuildingLevelChange(
                        gid,
                        level - 1,
                        level,
                        ref projectedWarehouse,
                        ref projectedGranary);
                    building = building with { Level = level, Gid = gid };
                    buildingsBySlot[buildingUpgrade.SlotId] = building;
                    if (gid is 10 or 11)
                    {
                        projection.StorageBySlot[buildingUpgrade.SlotId] = building;
                    }
                }

                if (targetLevel > stageStart)
                {
                    planned.Add(CreateBuildingUpgradeRequest(request, buildingUpgrade, targetLevel));
                }
                continue;
            }

            if (string.Equals(request.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                && ResourceUpgradePayload.TryFromDictionary(payload, out var resourceUpgrade, 20)
                && resourceUpgrade is not null)
            {
                if (!fieldsBySlot.TryGetValue(resourceUpgrade.SlotId, out var field)
                    || (BuildingCatalogService.GidForName(field.FieldType)
                        ?? BuildingCatalogService.GidForName(field.Name)
                        ?? BuildingCatalogService.GidForName(resourceUpgrade.Name)) is not int gid)
                {
                    return new ConstructionStoragePreflightResult(
                        planned,
                        allStorageUpgrades,
                        $"Resource slot {resourceUpgrade.SlotId} has no known resource type in the village snapshot.");
                }

                var currentLevel = field.Level ?? 0;
                var stageStart = currentLevel;
                for (var level = currentLevel + 1; level <= resourceUpgrade.TargetLevel; level++)
                {
                    var plannedCountBefore = planned.Count;
                    var storageCountBefore = allStorageUpgrades.Count;
                    var failure = EnsureCapacityForCost(
                        BuildingCatalogService.CostFor(gid, level),
                        null,
                        payload,
                        request,
                        projection,
                        buildingsBySlot,
                        ref projectedWarehouse,
                        ref projectedGranary,
                        planned,
                        allStorageUpgrades);
                    if (failure is not null)
                    {
                        return new ConstructionStoragePreflightResult(planned, allStorageUpgrades, failure);
                    }

                    if (allStorageUpgrades.Count > storageCountBefore && level - 1 > stageStart)
                    {
                        InsertUpgradeBeforeTrailingStorageRequests(
                            planned,
                            plannedCountBefore,
                            CreateResourceUpgradeRequest(request, resourceUpgrade, level - 1));
                        stageStart = level - 1;
                    }
                }

                if (resourceUpgrade.TargetLevel > stageStart)
                {
                    planned.Add(CreateResourceUpgradeRequest(request, resourceUpgrade, resourceUpgrade.TargetLevel));
                }
                fieldsBySlot[resourceUpgrade.SlotId] = field with { Level = resourceUpgrade.TargetLevel };
                continue;
            }

            planned.Add(CloneRequest(request));
            if (string.Equals(request.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
                && payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var rawBulkTarget)
                && int.TryParse(rawBulkTarget, out var bulkTarget))
            {
                foreach (var slot in fieldsBySlot.Keys.ToList())
                {
                    fieldsBySlot[slot] = fieldsBySlot[slot] with
                    {
                        Level = Math.Max(fieldsBySlot[slot].Level ?? 0, bulkTarget),
                    };
                }
            }
        }

        return new ConstructionStoragePreflightResult(planned, allStorageUpgrades);
    }

    private static string? EnsureCapacityForCost(
        BuildingLevelStats? cost,
        int? excludedStorageSlot,
        IReadOnlyDictionary<string, string> sourcePayload,
        QueueItemCreateRequest sourceRequest,
        StorageProjection projection,
        IDictionary<int, Building> buildingsBySlot,
        ref long projectedWarehouse,
        ref long projectedGranary,
        ICollection<QueueItemCreateRequest> planned,
        ICollection<StoragePreflightUpgrade> allStorageUpgrades)
    {
        if (cost is null)
        {
            return "The exact construction cost is missing from the building catalog.";
        }

        var upgrades = new List<StoragePreflightUpgrade>();
        var requiredWarehouse = Math.Max(cost.Wood, Math.Max(cost.Clay, cost.Iron));
        var failure = AddRequiredUpgrade(
            StorageCapacityKind.Warehouse,
            requiredWarehouse,
            ref projectedWarehouse,
            projection.StorageBySlot,
            projection.OccupiedBuildingSlots,
            upgrades,
            excludedStorageSlot);
        failure ??= AddRequiredUpgrade(
            StorageCapacityKind.Granary,
            cost.Crop,
            ref projectedGranary,
            projection.StorageBySlot,
            projection.OccupiedBuildingSlots,
            upgrades,
            excludedStorageSlot);
        if (failure is not null)
        {
            return failure;
        }

        if (upgrades.Count == 0)
        {
            return null;
        }

        var batchId = Guid.NewGuid().ToString();
        foreach (var upgrade in upgrades)
        {
            allStorageUpgrades.Add(upgrade);
            var name = upgrade.Kind.ToString();
            var gid = upgrade.Kind == StorageCapacityKind.Warehouse ? 10 : 11;
            if (upgrade.RequiresConstruction)
            {
                var constructPayload = new BuildingConstructPayload(upgrade.SlotId, gid, name).ToDictionary();
                CopyStorageRequestContext(sourcePayload, constructPayload, batchId);
                planned.Add(new QueueItemCreateRequest(
                    "construct_building",
                    constructPayload,
                    sourceRequest.Priority,
                    sourceRequest.MaxRetries));
            }

            if (!upgrade.RequiresConstruction || upgrade.TargetLevel > 1)
            {
                var upgradePayload = new BuildingUpgradePayload(upgrade.SlotId, upgrade.TargetLevel, name).ToDictionary();
                CopyStorageRequestContext(sourcePayload, upgradePayload, batchId);
                planned.Add(new QueueItemCreateRequest(
                    "upgrade_building_to_level",
                    upgradePayload,
                    sourceRequest.Priority,
                    sourceRequest.MaxRetries));
            }

            buildingsBySlot[upgrade.SlotId] = new Building(
                upgrade.SlotId,
                name,
                upgrade.TargetLevel,
                null,
                gid);
        }

        return null;
    }

    private static void CopyStorageRequestContext(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string batchId)
    {
        foreach (var key in new[]
                 {
                     BotOptionPayloadKeys.TargetVillageName,
                     BotOptionPayloadKeys.TargetVillageUrl,
                     BotOptionPayloadKeys.NpcTradeEnabled,
                 })
        {
            if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                target[key] = value;
            }
        }

        target[BotOptionPayloadKeys.StoragePreflightBatchId] = batchId;
        target[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByStorageCapacityPreflight;
    }

    private static QueueItemCreateRequest CreateBuildingUpgradeRequest(
        QueueItemCreateRequest source,
        BuildingUpgradePayload upgrade,
        int targetLevel)
    {
        var payload = new Dictionary<string, string>(
            source.Payload ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        payload[BotOptionPayloadKeys.BuildingUpgradeSlotId] = upgrade.SlotId.ToString();
        payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = targetLevel.ToString();
        if (!string.IsNullOrWhiteSpace(upgrade.Name))
        {
            payload[BotOptionPayloadKeys.BuildingUpgradeName] = upgrade.Name;
        }

        return new QueueItemCreateRequest(
            "upgrade_building_to_level",
            payload,
            source.Priority,
            source.MaxRetries);
    }

    private static QueueItemCreateRequest CreateResourceUpgradeRequest(
        QueueItemCreateRequest source,
        ResourceUpgradePayload upgrade,
        int targetLevel)
    {
        var payload = new Dictionary<string, string>(
            source.Payload ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        payload[BotOptionPayloadKeys.ResourceUpgradeSlotId] = upgrade.SlotId.ToString();
        payload[BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = targetLevel.ToString();
        return new QueueItemCreateRequest(
            "upgrade_resource_to_level",
            payload,
            source.Priority,
            source.MaxRetries);
    }

    private static QueueItemCreateRequest CloneRequest(QueueItemCreateRequest source)
        => new(
            source.TaskName,
            source.Payload is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(source.Payload, StringComparer.OrdinalIgnoreCase),
            source.Priority,
            source.MaxRetries);

    private static void InsertUpgradeBeforeTrailingStorageRequests(
        IList<QueueItemCreateRequest> planned,
        int insertionIndex,
        QueueItemCreateRequest upgrade)
        => planned.Insert(insertionIndex, upgrade);

    private static void ApplyStorageBuildingLevelChange(
        int gid,
        int previousLevel,
        int newLevel,
        ref long projectedWarehouse,
        ref long projectedGranary)
    {
        if (gid is not (10 or 11) || newLevel <= previousLevel)
        {
            return;
        }

        var added = StorageCapacityDependencyPlanner.CapacityAtLevel(newLevel)
            - StorageCapacityDependencyPlanner.CapacityAtLevel(previousLevel);
        if (gid == 10)
        {
            projectedWarehouse += added;
        }
        else
        {
            projectedGranary += added;
        }
    }

    private static void ApplyQueuedTargets(
        IEnumerable<QueueItem> queueItems,
        IDictionary<int, Building> buildingsBySlot,
        IDictionary<int, ResourceField> fieldsBySlot)
    {
        foreach (var item in queueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                && construct is not null)
            {
                buildingsBySlot[construct.SlotId] = new Building(
                    construct.SlotId,
                    construct.Name ?? BuildingCatalogService.NameForGid(construct.Gid),
                    1,
                    null,
                    construct.Gid);
                continue;
            }

            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
                && upgrade is not null
                && buildingsBySlot.TryGetValue(upgrade.SlotId, out var building)
                && (building.Gid ?? BuildingCatalogService.GidForName(upgrade.Name)) is int gid)
            {
                var target = upgrade.TargetLevel ?? BuildingCatalogService.MaxLevelFor(gid);
                buildingsBySlot[upgrade.SlotId] = building with
                {
                    Gid = gid,
                    Level = Math.Max(building.Level ?? 0, target),
                };
                continue;
            }

            if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                && ResourceUpgradePayload.TryFromDictionary(item.Payload, out var resource, 20)
                && resource is not null
                && fieldsBySlot.TryGetValue(resource.SlotId, out var field))
            {
                fieldsBySlot[resource.SlotId] = field with
                {
                    Level = Math.Max(field.Level ?? 0, resource.TargetLevel),
                };
                continue;
            }

            if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var rawTarget)
                && int.TryParse(rawTarget, out var targetLevel))
            {
                foreach (var slot in fieldsBySlot.Keys.ToList())
                {
                    fieldsBySlot[slot] = fieldsBySlot[slot] with
                    {
                        Level = Math.Max(fieldsBySlot[slot].Level ?? 0, targetLevel),
                    };
                }
            }
        }
    }

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
            projection.OccupiedBuildingSlots,
            upgrades);
        failure ??= AddRequiredUpgrade(
            StorageCapacityKind.Granary,
            requiredGranary,
            ref projectedGranary,
            storageBySlot,
            projection.OccupiedBuildingSlots,
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
                projection.OccupiedBuildingSlots,
                pendingUpgrades);
            failure ??= AddRequiredUpgrade(
                StorageCapacityKind.Granary,
                requiredGranary,
                ref projectedGranary,
                projection.StorageBySlot,
                projection.OccupiedBuildingSlots,
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
            .Select(building => (Building: building, Gid: ResolveStorageGid(building)))
            .Where(item => item.Gid is 10 or 11)
            .ToDictionary(
                item => item.Building.SlotId!.Value,
                item => item.Building with { Gid = item.Gid });
        var occupiedBuildingSlots = status.Buildings
            .Where(building => building.SlotId is >= 19 and <= 38)
            .Where(IsOccupiedBuildingSlot)
            .Select(building => building.SlotId!.Value)
            .ToHashSet();
        var projectedWarehouse = status.WarehouseCapacity ?? 0;
        var projectedGranary = status.GranaryCapacity ?? 0;

        foreach (var item in precedingQueueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                && construct is not null)
            {
                occupiedBuildingSlots.Add(construct.SlotId);
                if (construct.Gid is 10 or 11 && !storageBySlot.ContainsKey(construct.SlotId))
                {
                    storageBySlot[construct.SlotId] = new Building(
                        construct.SlotId,
                        BuildingCatalogService.NameForGid(construct.Gid),
                        1,
                        null,
                        construct.Gid);
                    var constructedCapacity = StorageCapacityDependencyPlanner.CapacityAtLevel(1)
                        - StorageCapacityDependencyPlanner.CapacityAtLevel(0);
                    if (construct.Gid == 10)
                    {
                        projectedWarehouse += constructedCapacity;
                    }
                    else
                    {
                        projectedGranary += constructedCapacity;
                    }
                }

                continue;
            }

            if ((!string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                || !BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
                || upgrade is null
                || !storageBySlot.TryGetValue(upgrade.SlotId, out var building)
                || building.Gid is not (10 or 11))
            {
                continue;
            }

            var currentLevel = building.Level ?? 0;
            var requestedLevel = upgrade.TargetLevel
                ?? BuildingCatalogService.MaxLevelFor(building.Gid.Value);
            var projectedLevel = Math.Max(currentLevel, requestedLevel);
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

        return new StorageProjection(
            storageBySlot,
            occupiedBuildingSlots,
            projectedWarehouse,
            projectedGranary);
    }

    private static string? AddRequiredUpgrade(
        StorageCapacityKind kind,
        long requiredCapacity,
        ref long projectedCapacity,
        IDictionary<int, Building> storageBySlot,
        ISet<int> occupiedBuildingSlots,
        ICollection<StoragePreflightUpgrade> upgrades,
        int? excludedSlotId = null)
    {
        if (requiredCapacity <= projectedCapacity)
        {
            return null;
        }

        var gid = kind == StorageCapacityKind.Warehouse ? 10 : 11;
        var candidate = storageBySlot.Values
            .Where(building => building.Gid == gid && building.Level is >= 1 and < 20)
            .Where(building => !excludedSlotId.HasValue || building.SlotId != excludedSlotId.Value)
            .OrderByDescending(building => building.Level)
            .FirstOrDefault();
        if (candidate?.SlotId is not int slotId || candidate.Level is not int currentLevel)
        {
            slotId = Enumerable.Range(19, 20).FirstOrDefault(slot => !occupiedBuildingSlots.Contains(slot));
            if (slotId == 0)
            {
                return $"No upgradeable {BuildingCatalogService.NameForGid(gid)} or free building slot is known in the village snapshot.";
            }

            currentLevel = 0;
            candidate = new Building(
                slotId,
                BuildingCatalogService.NameForGid(gid),
                currentLevel,
                null,
                gid);
            occupiedBuildingSlots.Add(slotId);
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
        HashSet<int> OccupiedBuildingSlots,
        long WarehouseCapacity,
        long GranaryCapacity);

    private static bool IsOccupiedBuildingSlot(Building building)
    {
        if ((building.Level ?? 0) > 0 || (building.Gid ?? 0) > 0)
        {
            return true;
        }

        var name = building.Name?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "Empty", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "g0", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ResolveStorageGid(Building building)
    {
        if (building.Gid is 10 or 11)
        {
            return building.Gid;
        }

        var byName = BuildingCatalogService.GidForName(building.Name);
        if (byName is 10 or 11)
        {
            return byName;
        }

        var normalized = string.Join(
                " ",
                (building.Name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();
        return normalized switch
        {
            "warehouse" => 10,
            "granary" or "granary / silo" or "silo" => 11,
            _ => null,
        };
    }
}
