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
}
