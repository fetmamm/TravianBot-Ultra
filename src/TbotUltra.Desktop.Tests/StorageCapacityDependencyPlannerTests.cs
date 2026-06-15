using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class StorageCapacityDependencyPlannerTests
{
    [Fact]
    public void ResolveBlock_PrefersWarehouseWhenBothCapacitiesAreTooLow()
    {
        var block = StorageCapacityDependencyPlanner.ResolveBlock(
            requiredWood: 5000,
            requiredClay: 4000,
            requiredIron: 3000,
            requiredCrop: 6000,
            warehouseCapacity: 4500,
            granaryCapacity: 4500);

        Assert.NotNull(block);
        Assert.Equal(StorageCapacityKind.Warehouse, block!.Kind);
        Assert.Equal(5000, block.RequiredCapacity);
    }

    [Fact]
    public void Plan_UpgradesExistingStorageOneLevel()
    {
        var status = CreateStatus(
            [new Building(20, "Warehouse", 5, null, 10)]);

        var plan = StorageCapacityDependencyPlanner.Plan(
            StorageCapacityKind.Warehouse,
            status,
            [],
            DateTimeOffset.UtcNow);

        Assert.Equal(StorageDependencyAction.Upgrade, plan.Action);
        Assert.Equal(20, plan.SlotId);
        Assert.Equal(6, plan.TargetLevel);
    }

    [Fact]
    public void Plan_MaxedStorageConstructsNewInFirstEmptySlot()
    {
        var buildings = Enumerable.Range(19, 20)
            .Where(slot => slot != 22)
            .Select(slot => slot == 20
                ? new Building(slot, "Warehouse", 20, null, 10)
                : new Building(slot, $"Building {slot}", 1, null, 15))
            .ToList();
        var status = CreateStatus(buildings);

        var plan = StorageCapacityDependencyPlanner.Plan(
            StorageCapacityKind.Warehouse,
            status,
            [],
            DateTimeOffset.UtcNow);

        Assert.Equal(StorageDependencyAction.Construct, plan.Action);
        Assert.Equal(22, plan.SlotId);
    }

    [Fact]
    public void Plan_NoEmptySlotPauses()
    {
        var buildings = Enumerable.Range(19, 20)
            .Select(slot => new Building(slot, $"Building {slot}", 1, null, 15))
            .ToList();
        buildings[0] = new Building(19, "Granary", 20, null, 11);
        var status = CreateStatus(buildings);

        var plan = StorageCapacityDependencyPlanner.Plan(
            StorageCapacityKind.Granary,
            status,
            [],
            DateTimeOffset.UtcNow);

        Assert.Equal(StorageDependencyAction.Pause, plan.Action);
    }

    [Fact]
    public void Plan_ActiveStorageConstructionWaitsForFinish()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var finish = TimerSnapshot.FromRemaining(90, now);
        var status = CreateStatus(
            [],
            [new ActiveConstruction(ConstructionKind.Building, "Granary", 1, 90, null, finish)]);

        var plan = StorageCapacityDependencyPlanner.Plan(
            StorageCapacityKind.Granary,
            status,
            [],
            now.AddSeconds(30));

        Assert.Equal(StorageDependencyAction.Wait, plan.Action);
        Assert.Equal(60, plan.WaitSeconds);
    }

    [Fact]
    public void Plan_SiloAliasUpgradesExistingGranary()
    {
        var status = CreateStatus(
            [new Building(20, "Silo", 5, null, null)]);

        var plan = StorageCapacityDependencyPlanner.Plan(
            StorageCapacityKind.Granary,
            status,
            [],
            DateTimeOffset.UtcNow);

        Assert.Equal(StorageDependencyAction.Upgrade, plan.Action);
        Assert.Equal(20, plan.SlotId);
        Assert.Equal(6, plan.TargetLevel);
    }

    [Fact]
    public void Plan_NewStorageSkipsSlotReservedByQueuedConstruction()
    {
        var status = CreateStatus(
            [new Building(19, "Warehouse", 20, null, 10)]);

        var plan = StorageCapacityDependencyPlanner.Plan(
            StorageCapacityKind.Warehouse,
            status,
            [20],
            DateTimeOffset.UtcNow);

        Assert.Equal(StorageDependencyAction.Construct, plan.Action);
        Assert.Equal(21, plan.SlotId);
    }

    private static VillageStatus CreateStatus(
        IReadOnlyList<Building> buildings,
        IReadOnlyList<ActiveConstruction>? activeConstructions = null)
    {
        return new VillageStatus(
            ActiveVillage: "Village",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: [],
            ActiveConstructions: activeConstructions ?? []);
    }
}
