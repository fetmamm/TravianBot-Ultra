using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class StorageCapacityQueuePreflightPlannerTests
{
    [Fact]
    public void PlanUpgradeAllResources_LiveIronLevelSixRequiresWarehouseLevelTwo()
    {
        var status = CreateStatus(
            [new ResourceField(4, "Iron Mine", "Iron Mine", 5, null)],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResources(status, [], 6);

        var upgrade = Assert.Single(result.Upgrades);
        Assert.Equal(StorageCapacityKind.Warehouse, upgrade.Kind);
        Assert.Equal(19, upgrade.SlotId);
        Assert.Equal(2, upgrade.TargetLevel);
        Assert.Equal(1_300, upgrade.RequiredCapacity);
    }

    [Fact]
    public void PlanUpgradeAllResources_LevelTenPlansBothStorageTypesRegardlessOfSmartOrder()
    {
        var fields = Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(
                slot,
                slot % 4 == 0 ? "Cropland" : slot % 3 == 0 ? "Iron Mine" : slot % 2 == 0 ? "Clay Pit" : "Woodcutter",
                "Resource field",
                1,
                null))
            .ToList();
        var status = CreateStatus(fields, warehouseCapacity: 1_200, granaryCapacity: 1_200);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResources(status, [], 10);

        Assert.Contains(result.Upgrades, upgrade => upgrade.Kind == StorageCapacityKind.Warehouse);
        Assert.Contains(result.Upgrades, upgrade => upgrade.Kind == StorageCapacityKind.Granary);
        Assert.True(result.ProjectedWarehouseCapacity >= result.RequiredWarehouseCapacity);
        Assert.True(result.ProjectedGranaryCapacity >= result.RequiredGranaryCapacity);
    }

    [Fact]
    public void PlanUpgradeAllResources_AlreadyQueuedWarehouseUpgradeIsIncluded()
    {
        var status = CreateStatus(
            [new ResourceField(4, "Iron Mine", "Iron Mine", 5, null)],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200);
        var queuedUpgrade = new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Status = QueueStatus.Pending,
            Payload = new BuildingUpgradePayload(19, 2, "Warehouse").ToDictionary(),
        };

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResources(
            status,
            [queuedUpgrade],
            6);

        Assert.Empty(result.Upgrades);
        Assert.Equal(1_700, result.ProjectedWarehouseCapacity);
    }

    [Fact]
    public void PlanUpgradeAllResources_RecognizesStorageByNameWhenSnapshotGidIsMissing()
    {
        var status = CreateStatusWithoutStorage(
            [new ResourceField(4, "Iron Mine", "Iron Mine", 5, null)],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200,
            buildings:
            [
                new Building(19, "Warehouse", 1, null, null),
                new Building(20, "Granary", 1, null, null),
            ]);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResources(status, [], 6);

        var upgrade = Assert.Single(result.Upgrades);
        Assert.False(upgrade.RequiresConstruction);
        Assert.Equal(19, upgrade.SlotId);
    }

    [Fact]
    public void PlanUpgradeAllResourcesStepwise_InsertsStorageOnlyAtEachCapacityBoundary()
    {
        var fields = Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(
                slot,
                slot % 4 == 0 ? "Cropland" : slot % 3 == 0 ? "Iron Mine" : slot % 2 == 0 ? "Clay Pit" : "Woodcutter",
                "Resource field",
                1,
                null))
            .ToList();
        var status = CreateStatus(fields, warehouseCapacity: 1_200, granaryCapacity: 1_200);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(status, [], 10);

        Assert.Null(result.CannotPlanReason);
        Assert.True(result.Stages.Count > 1);
        Assert.Equal(10, result.Stages[^1].ResourceTargetLevel);
        Assert.True(result.Stages.Zip(result.Stages.Skip(1)).All(pair =>
            pair.First.ResourceTargetLevel < pair.Second.ResourceTargetLevel));

        var warehouseTargets = result.Upgrades
            .Where(upgrade => upgrade.Kind == StorageCapacityKind.Warehouse)
            .Select(upgrade => upgrade.TargetLevel)
            .ToList();
        var granaryTargets = result.Upgrades
            .Where(upgrade => upgrade.Kind == StorageCapacityKind.Granary)
            .Select(upgrade => upgrade.TargetLevel)
            .ToList();
        Assert.True(warehouseTargets.Count > 1);
        Assert.True(granaryTargets.Count > 1);
        Assert.True(warehouseTargets.Zip(warehouseTargets.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.True(granaryTargets.Zip(granaryTargets.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [Fact]
    public void PlanUpgradeAllResourcesStepwise_QueuedBulkAndStorageBecomeTheProjectedStartingPoint()
    {
        var fields = Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(slot, "Iron Mine", "Iron Mine", 1, null))
            .ToList();
        var status = CreateStatus(fields, warehouseCapacity: 1_200, granaryCapacity: 1_200);
        var queuedWarehouse = new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Status = QueueStatus.Pending,
            Payload = new BuildingUpgradePayload(19, 2, "Warehouse").ToDictionary(),
        };
        var queuedBulk = new QueueItem
        {
            TaskName = "upgrade_all_resources_to_level",
            Status = QueueStatus.Pending,
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "6",
            },
        };

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(
            status,
            [queuedWarehouse, queuedBulk],
            7);

        var stage = Assert.Single(result.Stages);
        Assert.Equal(7, stage.ResourceTargetLevel);
        Assert.DoesNotContain(stage.StorageUpgradesBefore, upgrade =>
            upgrade.Kind == StorageCapacityKind.Warehouse && upgrade.TargetLevel <= 2);
    }

    [Fact]
    public void PlanUpgradeAllResourcesStepwise_MissingWarehouseConstructsInFreeSlotThenUpgrades()
    {
        var status = CreateStatusWithoutStorage(
            [new ResourceField(4, "Iron Mine", "Iron Mine", 5, null)],
            warehouseCapacity: 800,
            granaryCapacity: 1_200,
            buildings: [new Building(20, "Granary", 1, null, 11)]);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(status, [], 6);

        var warehouse = Assert.Single(result.Upgrades);
        Assert.Equal(StorageCapacityKind.Warehouse, warehouse.Kind);
        Assert.True(warehouse.RequiresConstruction);
        Assert.Equal(19, warehouse.SlotId);
        Assert.Equal(2, warehouse.TargetLevel);
    }

    [Fact]
    public void PlanUpgradeAllResourcesStepwise_StorageLevelsAheadAddsExtraCapacityLevels()
    {
        var status = CreateStatus(
            [new ResourceField(4, "Iron Mine", "Iron Mine", 5, null)],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(
            status,
            [],
            targetLevel: 6,
            storageUpgradeLevelsAhead: 2);

        var warehouse = Assert.Single(result.Upgrades);
        Assert.Equal(StorageCapacityKind.Warehouse, warehouse.Kind);
        Assert.Equal(3, warehouse.TargetLevel);
    }

    [Fact]
    public void PlanUpgradeAllResourcesStepwise_MissingBothStorageBuildingsUsesDifferentFreeSlots()
    {
        var fields = Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(
                slot,
                slot % 4 == 0 ? "Cropland" : "Iron Mine",
                "Resource field",
                1,
                null))
            .ToList();
        var status = CreateStatusWithoutStorage(
            fields,
            warehouseCapacity: 800,
            granaryCapacity: 800,
            buildings: [new Building(21, "Main Building", 1, null, 15)]);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(status, [], 10);

        Assert.Null(result.CannotPlanReason);
        var constructions = result.Upgrades.Where(upgrade => upgrade.RequiresConstruction).ToList();
        Assert.Contains(constructions, upgrade => upgrade.Kind == StorageCapacityKind.Warehouse);
        Assert.Contains(constructions, upgrade => upgrade.Kind == StorageCapacityKind.Granary);
        Assert.Equal(constructions.Count, constructions.Select(upgrade => upgrade.SlotId).Distinct().Count());
    }

    [Fact]
    public void PlanUpgradeAllResourcesStepwise_BaseGranaryIsEnoughForLevelSixButNotLevelSeven()
    {
        var fields = Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(
                slot,
                slot % 4 == 0 ? "Cropland" : slot % 3 == 0 ? "Iron Mine" : slot % 2 == 0 ? "Clay Pit" : "Woodcutter",
                "Resource field",
                1,
                null))
            .ToList();
        var status = CreateStatusWithoutStorage(
            fields,
            warehouseCapacity: 800,
            granaryCapacity: 800,
            buildings: [new Building(21, "Main Building", 1, null, 15)]);

        var result = StorageCapacityQueuePreflightPlanner.PlanUpgradeAllResourcesStepwise(status, [], 7);

        Assert.Null(result.CannotPlanReason);
        var levelSix = Assert.Single(result.Stages, stage => stage.ResourceTargetLevel == 6);
        Assert.DoesNotContain(levelSix.StorageUpgradesBefore, upgrade =>
            upgrade.Kind == StorageCapacityKind.Granary);
        var levelSeven = Assert.Single(result.Stages, stage => stage.ResourceTargetLevel == 7);
        var granary = Assert.Single(levelSeven.StorageUpgradesBefore, upgrade =>
            upgrade.Kind == StorageCapacityKind.Granary);
        Assert.True(granary.RequiresConstruction);
        Assert.Equal(1_300, granary.RequiredCapacity);
    }

    [Fact]
    public void PlanConstructionRequestsStepwise_MainBuildingToTwentyStagesStorageBeforeBlockedLevels()
    {
        var status = CreateStatusWithoutStorage(
            [],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200,
            buildings:
            [
                new Building(19, "Warehouse", 1, null, 10),
                new Building(20, "Granary", 1, null, 11),
                new Building(21, "Main Building", 1, null, 15),
            ]);
        var payload = new BuildingUpgradePayload(21, 20, "Main Building").ToDictionary();

        var result = StorageCapacityQueuePreflightPlanner.PlanConstructionRequestsStepwise(
            status,
            [],
            [new QueueItemCreateRequest("upgrade_building_to_level", payload, 0, 3)]);

        Assert.Null(result.CannotPlanReason);
        Assert.NotEmpty(result.Upgrades);
        Assert.Contains(result.Upgrades, upgrade => upgrade.Kind == StorageCapacityKind.Warehouse);
        Assert.Contains(result.Upgrades, upgrade => upgrade.Kind == StorageCapacityKind.Granary);
        var finalUpgrade = result.Requests.Last(request =>
            string.Equals(request.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            && request.Payload![BotOptionPayloadKeys.BuildingUpgradeSlotId] == "21");
        Assert.Equal("20", finalUpgrade.Payload![BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
        var orderedRequests = result.Requests.ToList();
        Assert.True(orderedRequests.FindIndex(request => request.Payload?.GetValueOrDefault(BotOptionPayloadKeys.AutoAddedBy) == BotOptionPayloadKeys.AutoAddedByStorageCapacityPreflight)
            < orderedRequests.IndexOf(finalUpgrade));
    }

    [Fact]
    public void PlanConstructionRequestsStepwise_RallyPointStorageExplainsEachBlockedLevel()
    {
        var status = CreateStatusWithoutStorage(
            [],
            warehouseCapacity: 1_700,
            granaryCapacity: 1_200,
            buildings:
            [
                new Building(19, "Warehouse", 2, null, 10),
                new Building(24, "Granary", 1, null, 11),
                new Building(39, "Rally Point", 10, null, 16),
            ]);
        var payload = new BuildingUpgradePayload(39, 20, "Rally Point").ToDictionary();

        var result = StorageCapacityQueuePreflightPlanner.PlanConstructionRequestsStepwise(
            status,
            [],
            [new QueueItemCreateRequest("upgrade_building_to_level", payload, 0, 3)]);

        Assert.Null(result.CannotPlanReason);
        Assert.Contains(result.Upgrades, upgrade =>
            upgrade.Kind == StorageCapacityKind.Warehouse
            && upgrade.TargetLevel == 3
            && upgrade.RequiredBy == "Rally Point level 11");
        Assert.Contains(result.Upgrades, upgrade =>
            upgrade.Kind == StorageCapacityKind.Warehouse
            && upgrade.TargetLevel == 4
            && upgrade.RequiredBy == "Rally Point level 12");
        Assert.Contains(result.Upgrades, upgrade =>
            upgrade.Kind == StorageCapacityKind.Granary
            && upgrade.TargetLevel == 2
            && upgrade.RequiredBy == "Rally Point level 13");
    }

    [Fact]
    public void PlanConstructionRequestsStepwise_SingleResourceUpgradeAlsoPlansStorage()
    {
        var status = CreateStatus(
            [new ResourceField(4, "Iron Mine", "Iron Mine", 5, null)],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200);
        var payload = new ResourceUpgradePayload(4, 6, "Iron Mine").ToDictionary();

        var result = StorageCapacityQueuePreflightPlanner.PlanConstructionRequestsStepwise(
            status,
            [],
            [new QueueItemCreateRequest("upgrade_resource_to_level", payload, 0, 3)]);

        var storage = Assert.Single(result.Upgrades);
        Assert.Equal(StorageCapacityKind.Warehouse, storage.Kind);
        Assert.Equal("upgrade_resource_to_level", result.Requests[^1].TaskName);
    }

    [Fact]
    public void PlanConstructionRequestsStepwise_NewExpensiveBuildingPlansStorageBeforeConstruct()
    {
        var status = CreateStatusWithoutStorage(
            [],
            warehouseCapacity: 1_200,
            granaryCapacity: 1_200,
            buildings:
            [
                new Building(19, "Warehouse", 1, null, 10),
                new Building(20, "Granary", 1, null, 11),
                new Building(21, "Main Building", 10, null, 15),
            ]);
        var payload = new BuildingConstructPayload(22, 27, "Treasury").ToDictionary();

        var result = StorageCapacityQueuePreflightPlanner.PlanConstructionRequestsStepwise(
            status,
            [],
            [new QueueItemCreateRequest("construct_building", payload, 0, 3)]);

        Assert.Null(result.CannotPlanReason);
        Assert.NotEmpty(result.Upgrades);
        var constructIndex = result.Requests.ToList().FindIndex(request =>
            request.TaskName == "construct_building"
            && request.Payload![BotOptionPayloadKeys.BuildingConstructSlotId] == "22");
        var storageIndex = result.Requests.ToList().FindIndex(request =>
            request.Payload?.GetValueOrDefault(BotOptionPayloadKeys.AutoAddedBy)
            == BotOptionPayloadKeys.AutoAddedByStorageCapacityPreflight);
        Assert.InRange(storageIndex, 0, constructIndex - 1);
    }

    private static VillageStatus CreateStatus(
        IReadOnlyList<ResourceField> fields,
        long warehouseCapacity,
        long granaryCapacity)
    {
        return new VillageStatus(
            ActiveVillage: "Village",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: fields,
            Buildings:
            [
                new Building(19, "Warehouse", 1, null, 10),
                new Building(20, "Granary", 1, null, 11),
            ],
            BuildQueue: [],
            WarehouseCapacity: warehouseCapacity,
            GranaryCapacity: granaryCapacity);
    }

    private static VillageStatus CreateStatusWithoutStorage(
        IReadOnlyList<ResourceField> fields,
        long warehouseCapacity,
        long granaryCapacity,
        IReadOnlyList<Building> buildings)
        => new(
            ActiveVillage: "Village",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: fields,
            Buildings: buildings,
            BuildQueue: [],
            WarehouseCapacity: warehouseCapacity,
            GranaryCapacity: granaryCapacity);
}
