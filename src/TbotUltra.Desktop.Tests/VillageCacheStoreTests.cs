using System.Collections.Generic;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageCacheStoreTests : IDisposable
{
    private readonly string _root;
    private string _activeAccount = "alice";

    public VillageCacheStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-village-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private static VillageStatus MakeStatus(string name)
    {
        return new VillageStatus(
            ActiveVillage: name,
            Villages: new[] { new Village(name, $"dorf1.php?newdid=1", IsCapital: false, CoordX: 1, CoordY: 2) },
            Resources: new Dictionary<string, string> { ["wood"] = "999" },
            ResourceFields: new[] { new ResourceField(1, "wood", "Woodcutter", 5, "build.php?id=1") },
            Buildings: new[] { new Building(26, "Main Building", 3, "build.php?id=26", 15) },
            BuildQueue: new[] { new BuildQueueItem("something", "00:10:00") },
            Tribe: "Gauls",
            VillageCount: 2,
            Gold: 46,
            WarehouseCapacity: 4000,
            IsCapital: false);
    }

    [Fact]
    public void SaveThenLoad_PreservesBuildingsAndFields_AndStripsVolatile()
    {
        var store = CreateStore();
        var cache = new Dictionary<string, VillageStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["GREZ"] = MakeStatus("GREZ"),
            ["SLAV"] = MakeStatus("SLAV"),
        };

        store.Save(cache);
        var loaded = CreateStore().Load();

        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.ContainsKey("GREZ"));
        var grez = loaded["GREZ"];
        // Durable structure preserved.
        Assert.Single(grez.Buildings);
        Assert.Equal("Main Building", grez.Buildings[0].Name);
        Assert.Equal(3, grez.Buildings[0].Level);
        Assert.Single(grez.ResourceFields);
        Assert.Equal(5, grez.ResourceFields[0].Level);
        Assert.Equal("Gauls", grez.Tribe);
        // Volatile values stripped.
        Assert.Empty(grez.Resources);
        Assert.Empty(grez.BuildQueue);
        Assert.Null(grez.Gold);
        // Capacity is remembered (storage capacity is durable until the warehouse is upgraded).
        Assert.Equal(4000, grez.WarehouseCapacity);
    }

    [Fact]
    public void SaveThenLoad_RecomputesFutureTimersFromAbsoluteFinish()
    {
        var finish = TimerSnapshot.FromRemaining(120);
        var status = MakeStatus("GREZ") with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 4, 120, "00:02:00", finish),
            ],
            BuildQueueFinish = finish,
            TroopTrainingQueues =
            [
                new TroopTrainingQueueStatus(
                    TbotUltra.Core.Travian.TroopTrainingBuildingType.Barracks,
                    "Barracks",
                    true,
                    29,
                    [],
                    120,
                    "00:02:00",
                    finish),
            ],
        };

        CreateStore().Save(new Dictionary<string, VillageStatus> { ["GREZ"] = status });
        var loaded = CreateStore().Load()["GREZ"];

        Assert.Single(loaded.ActiveConstructions!);
        Assert.InRange(loaded.ActiveConstructions![0].TimeLeftSeconds!.Value, 1, 120);
        Assert.InRange(loaded.BuildQueueRemainingSeconds!.Value, 1, 120);
        Assert.Single(loaded.TroopTrainingQueues!);
        Assert.InRange(loaded.TroopTrainingQueues![0].RemainingSeconds!.Value, 1, 120);
    }

    [Fact]
    public void Load_KeepsExpiredConstructionSnapshotButDoesNotCountItActive()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = new TimerSnapshot(30, now.AddMinutes(-2), now.AddMinutes(-1), false);
        var logs = new List<string>();
        var status = MakeStatus("GREZ") with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 4, 30, "00:00:30", expired),
            ],
            BuildQueueFinish = expired,
        };

        new VillageCacheStore(_root, () => _activeAccount, logs.Add)
            .Save(new Dictionary<string, VillageStatus> { ["GREZ"] = status });
        var loaded = new VillageCacheStore(_root, () => _activeAccount, logs.Add).Load()["GREZ"];

        Assert.Single(loaded.ActiveConstructions!);
        Assert.Null(loaded.BuildQueueRemainingSeconds);
        Assert.False(loaded.IsBuildingInProgress);
        Assert.Equal(0, loaded.ActiveBuildCount);
        Assert.Contains(logs, line =>
            line.Contains("stale timer", StringComparison.OrdinalIgnoreCase)
            && line.Contains("GREZ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SaveThenLoad_RecomputesSmithyQueueFromAbsoluteFinish()
    {
        var finish = TimerSnapshot.FromRemaining(120);
        var status = MakeStatus("GREZ") with
        {
            SmithyUpgradeStatus = new SmithyUpgradeStatus(
                SmithyExists: true,
                SmithySlotId: 21,
                ActiveUpgradeCount: 1,
                RemainingSeconds: 120,
                ActiveUpgradeRemainingSeconds: [120],
                RemainingText: "00:02:00",
                StatusText: "Active",
                ActiveUpgradeFinishes: [finish],
                ActiveUpgrades: [new ActiveSmithyUpgrade("Phalanx", 4, 120, finish)]),
        };

        CreateStore().Save(new Dictionary<string, VillageStatus> { ["GREZ"] = status });
        var loaded = CreateStore().Load()["GREZ"].SmithyUpgradeStatus!;

        var active = Assert.Single(loaded.ActiveUpgrades!);
        Assert.Equal("Phalanx", active.Name);
        Assert.Equal(4, active.TargetLevel);
        Assert.InRange(active.TimeLeftSeconds!.Value, 1, 120);
    }

    [Fact]
    public void Load_NoFile_ReturnsEmpty()
    {
        Assert.Empty(CreateStore().Load());
    }

    [Fact]
    public void Cache_IsScopedPerAccount()
    {
        _activeAccount = "alice";
        CreateStore().Save(new Dictionary<string, VillageStatus> { ["GREZ"] = MakeStatus("GREZ") });

        _activeAccount = "bob";
        Assert.Empty(CreateStore().Load());
    }

    private VillageCacheStore CreateStore() => new(_root, () => _activeAccount);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
