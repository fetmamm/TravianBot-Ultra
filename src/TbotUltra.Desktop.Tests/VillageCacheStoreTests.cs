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
        Assert.Null(grez.WarehouseCapacity);
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
