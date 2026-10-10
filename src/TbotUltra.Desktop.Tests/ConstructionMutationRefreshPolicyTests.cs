using System.Collections.Generic;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionMutationRefreshPolicyTests
{
    [Fact]
    public void AcceptedCurrentDorf2Refresh_SatisfiesPendingConstructionStatusSync()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src", "TbotUltra.Desktop", "MainWindow.QueueExecution.cs"));
        var start = source.IndexOf(
            "private async Task<(bool BuildingsStatusRead, bool StorageStatusRead)> RefreshConstructionStatusAfterBuildingMutationAsync(",
            StringComparison.Ordinal);
        var end = source.IndexOf("    // dorf1 counterpart", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        var accepted = method.IndexOf("ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot", StringComparison.Ordinal);
        var cached = method.IndexOf("CacheVillageStatus(merged);", StringComparison.Ordinal);
        var synchronized = method.IndexOf("_automationDesk.MarkConstructionStatusSynchronized();", StringComparison.Ordinal);

        Assert.True(accepted >= 0);
        Assert.True(cached > accepted);
        Assert.True(synchronized > cached);
    }

    [Fact]
    public void CanUseCurrentDorf2Snapshot_RequiresCompleteAuthoritativeMatchingVillage()
    {
        var status = MakeCurrentDorf2Status();

        Assert.True(ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot(status, "xy:157|-114"));
        Assert.False(ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot(
            status with { Buildings = status.Buildings.Take(21).ToList() },
            "xy:157|-114"));
        Assert.False(ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot(
            status with { ActiveConstructionsFromOverview = false },
            "xy:157|-114"));
        Assert.False(ConstructionMutationRefreshPolicy.CanUseCurrentDorf2Snapshot(status, "xy:158|-114"));
    }

    [Fact]
    public void MergeCurrentDorf2Snapshot_ReplacesLiveConstructionData_AndPreservesDorf1State()
    {
        var existing = MakeCurrentDorf2Status() with
        {
            Villages = [new Village("T4", "dorf1.php?newdid=4", false, 157, -114)],
            Resources = new Dictionary<string, string> { ["wood"] = "100" },
            ResourceFields = [new ResourceField(1, "Wood", "Woodcutter", 7, "build.php?id=1")],
            Gold = 25,
            UnreadMessages = 3,
            Buildings = MakeBuildings(levelAtSlot28: 0),
            ActiveConstructions = [],
            ActiveBuildCount = 0,
            IsBuildingInProgress = false,
        };
        var current = MakeCurrentDorf2Status() with
        {
            Resources = new Dictionary<string, string> { ["wood"] = "90" },
            Buildings = MakeBuildings(levelAtSlot28: 1),
            ActiveConstructions =
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Sawmill",
                    1,
                    90,
                    null,
                    SlotId: 28,
                    Gid: 5),
            ],
            ActiveBuildCount = 1,
            IsBuildingInProgress = true,
        };

        var merged = ConstructionMutationRefreshPolicy.MergeCurrentDorf2Snapshot(existing, current);

        Assert.Equal("90", merged.Resources["wood"]);
        Assert.Single(merged.ResourceFields);
        Assert.Single(merged.Villages);
        Assert.Equal(25, merged.Gold);
        Assert.Equal(3, merged.UnreadMessages);
        Assert.Equal(1, Assert.Single(merged.Buildings, building => building.SlotId == 28).Level);
        Assert.Single(merged.ActiveConstructions!);
        Assert.True(merged.IsBuildingInProgress);
    }

    private static VillageStatus MakeCurrentDorf2Status() => new(
        ActiveVillage: "T4",
        Villages: [],
        Resources: new Dictionary<string, string>(),
        ResourceFields: [],
        Buildings: MakeBuildings(levelAtSlot28: 1),
        BuildQueue: [],
        ActiveConstructions: [],
        ActiveConstructionsFromOverview: true,
        ActiveVillageCoordX: 157,
        ActiveVillageCoordY: -114);

    private static IReadOnlyList<Building> MakeBuildings(int levelAtSlot28) =>
        Enumerable.Range(19, 22)
            .Select(slot => new Building(
                slot,
                slot == 28 ? "Sawmill" : string.Empty,
                slot == 28 ? levelAtSlot28 : 0,
                $"build.php?id={slot}",
                slot == 28 ? 5 : null))
            .ToList();
}
