using TbotUltra.Worker.Domain;
using System.Text.Json;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BuildingSnapshotTests
{
    [Fact]
    public void FromVillageStatus_PreservesActiveLevelOneConstruction()
    {
        var construction = new ActiveConstruction(
            ConstructionKind.Building,
            "Warehouse",
            1,
            120,
            "0:02:00 hrs.",
            SlotId: null,
            Gid: null);
        var status = new VillageStatus(
            ActiveVillage: "ROM",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: [new Building(34, "Warehouse", 0, "build.php?id=34", 10)],
            BuildQueue: [],
            IsBuildingInProgress: true,
            ActiveBuildCount: 1,
            ActiveConstructions: [construction],
            ActiveConstructionsFromOverview: true);

        var payload = BuildingSnapshot.FromVillageStatus("account", status);
        var snapshot = JsonSerializer.Deserialize<BuildingSnapshot>(JsonSerializer.Serialize(payload));

        Assert.NotNull(snapshot);
        Assert.Equal([construction], snapshot.ActiveConstructions);
        Assert.True(snapshot.ActiveConstructionsFromOverview);
    }
}
