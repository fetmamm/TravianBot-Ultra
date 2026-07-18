using System.Collections.Generic;
using System.Threading.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageStatusCacheTests
{
    private static VillageStatus MakeStatus(string name, int? coordX = 1, int? coordY = 2)
    {
        return new VillageStatus(
            ActiveVillage: name,
            Villages: new[] { new Village(name, "dorf1.php?newdid=1", IsCapital: false, CoordX: coordX, CoordY: coordY) },
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: [],
            BuildQueue: []);
    }

    [Fact]
    public void Set_StoresUnderCoordinateKey_AndBothLookupsFindIt()
    {
        var cache = new VillageStatusCache();

        cache.Set("GREZ", MakeStatus("GREZ", 5, -7));

        Assert.True(cache.TryGetByKey("xy:5|-7", out var byKey));
        Assert.True(cache.TryGetByName("GREZ", out var byName));
        Assert.Same(byKey, byName);
        Assert.True(cache.Snapshot.ContainsKey("xy:5|-7"));
    }

    [Fact]
    public void Set_WithoutResolvableCoordinates_FallsBackToNameKey()
    {
        var cache = new VillageStatusCache();

        cache.Set("GREZ", MakeStatus("GREZ", null, null));

        Assert.True(cache.TryGetByName("GREZ", out _));
        Assert.True(cache.Snapshot.ContainsKey("GREZ"));
    }

    [Fact]
    public void Set_PartialStatusWithoutVillageList_ReusesExistingKeyMapping()
    {
        var cache = new VillageStatusCache();
        cache.Set("GREZ", MakeStatus("GREZ", 5, -7));

        // A lightweight refresh may carry no village list; it must still land on the canonical entry.
        var partial = MakeStatus("GREZ") with { Villages = [] };
        cache.Set("GREZ", partial);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetByKey("xy:5|-7", out var status));
        Assert.Same(partial, status);
    }

    [Fact]
    public void Set_CanonicalWriteSupersedesLegacyNameEntry()
    {
        var cache = new VillageStatusCache();
        cache.LoadFrom(new Dictionary<string, VillageStatus> { ["GREZ"] = MakeStatus("GREZ", null, null) });

        cache.Set("GREZ", MakeStatus("GREZ", 5, -7));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.Snapshot.ContainsKey("xy:5|-7"));
        Assert.False(cache.Snapshot.ContainsKey("GREZ"));
    }

    [Fact]
    public void MigrateName_MovesNameLookupForCanonicalEntry()
    {
        var cache = new VillageStatusCache();
        cache.Set("Old name", MakeStatus("Old name", 5, -7));

        Assert.True(cache.MigrateName("Old name", "New name"));

        Assert.True(cache.TryGetByName("New name", out _));
        Assert.False(cache.TryGetByName("Old name", out _));
        // The entry itself stays under its coordinate key.
        Assert.True(cache.TryGetByKey("xy:5|-7", out _));
    }

    [Fact]
    public void MigrateName_RekeysLegacyNameEntry()
    {
        var cache = new VillageStatusCache();
        cache.LoadFrom(new Dictionary<string, VillageStatus> { ["Old name"] = MakeStatus("Old name", null, null) });

        Assert.True(cache.MigrateName("Old name", "New name"));

        Assert.True(cache.TryGetByName("New name", out _));
        Assert.False(cache.TryGetByName("Old name", out _));
    }

    [Fact]
    public void Set_PrefersActiveVillageCoordinates_EvenWithDuplicateNames()
    {
        var cache = new VillageStatusCache();
        // Two villages share the name "TWIN"; name matching alone cannot tell them apart, but each
        // status carries its own sidebar coordinates (data-x/data-y).
        var twins = new[]
        {
            new Village("TWIN", "dorf1.php?newdid=1", IsCapital: false, CoordX: 1, CoordY: 1),
            new Village("TWIN", "dorf1.php?newdid=2", IsCapital: false, CoordX: 2, CoordY: 2),
        };
        var first = MakeStatus("TWIN") with { Villages = twins, ActiveVillageCoordX = 1, ActiveVillageCoordY = 1 };
        var second = MakeStatus("TWIN") with { Villages = twins, ActiveVillageCoordX = 2, ActiveVillageCoordY = 2 };

        cache.Set("TWIN", first);
        cache.Set("TWIN", second);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetByKey("xy:1|1", out var storedFirst));
        Assert.True(cache.TryGetByKey("xy:2|2", out var storedSecond));
        Assert.Same(first, storedFirst);
        Assert.Same(second, storedSecond);
        Assert.False(cache.TryGetByName("TWIN", out _));
        Assert.False(cache.TryGetUniqueKeyByName("TWIN", out _));
    }

    [Fact]
    public void MigrateName_WithVillageKey_RenamesOnlyRequestedTwin()
    {
        var cache = new VillageStatusCache();
        cache.Set("TWIN", MakeStatus("TWIN", 1, 1) with { ActiveVillageCoordX = 1, ActiveVillageCoordY = 1 });
        cache.Set("TWIN", MakeStatus("TWIN", 2, 2) with { ActiveVillageCoordX = 2, ActiveVillageCoordY = 2 });

        Assert.True(cache.MigrateName("TWIN", "RENAMED", "xy:2|2"));

        Assert.Equal("TWIN", cache.Snapshot["xy:1|1"].ActiveVillage);
        Assert.Equal("RENAMED", cache.Snapshot["xy:2|2"].ActiveVillage);
    }

    [Fact]
    public void ConcurrentLoopReadsAndUiWrites_KeepDuplicateVillagesSeparated()
    {
        var cache = new VillageStatusCache();
        var first = MakeStatus("TWIN", 1, 1) with { ActiveVillageCoordX = 1, ActiveVillageCoordY = 1 };
        var second = MakeStatus("TWIN", 2, 2) with { ActiveVillageCoordX = 2, ActiveVillageCoordY = 2 };

        Parallel.For(0, 500, index =>
        {
            cache.Set("TWIN", index % 2 == 0 ? first : second);
            _ = cache.Snapshot.Count;
            _ = cache.Values;
            cache.TryGetByKey(index % 2 == 0 ? "xy:1|1" : "xy:2|2", out _);
            cache.TryGetByName("TWIN", out _);
        });

        Assert.True(cache.TryGetByKey("xy:1|1", out var storedFirst));
        Assert.True(cache.TryGetByKey("xy:2|2", out var storedSecond));
        Assert.Equal(1, storedFirst.ActiveVillageCoordX);
        Assert.Equal(2, storedSecond.ActiveVillageCoordX);
        Assert.False(cache.TryGetByName("TWIN", out _));
    }

    [Fact]
    public void Set_AmbiguousPartialStatus_DoesNotCreateOrOverwriteTwinEntry()
    {
        var cache = new VillageStatusCache();
        var first = MakeStatus("TWIN", 1, 1) with { ActiveVillageCoordX = 1, ActiveVillageCoordY = 1 };
        var second = MakeStatus("TWIN", 2, 2) with { ActiveVillageCoordX = 2, ActiveVillageCoordY = 2 };
        cache.Set("TWIN", first);
        cache.Set("TWIN", second);

        cache.Set("TWIN", MakeStatus("TWIN", null, null) with { Villages = [] });

        Assert.Equal(2, cache.Count);
        Assert.Same(first, cache.Snapshot["xy:1|1"]);
        Assert.Same(second, cache.Snapshot["xy:2|2"]);
    }

    [Fact]
    public void LoadFrom_IndexesEntriesByTheirOwnVillageName()
    {
        var cache = new VillageStatusCache();

        cache.LoadFrom(new Dictionary<string, VillageStatus>
        {
            ["xy:5|-7"] = MakeStatus("GREZ", 5, -7),
        });

        Assert.True(cache.TryGetByName("GREZ", out var status));
        Assert.Equal("GREZ", status.ActiveVillage);
    }
}
