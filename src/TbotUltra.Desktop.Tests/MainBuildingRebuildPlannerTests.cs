using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class MainBuildingRebuildPlannerTests
{
    [Fact]
    public void Plan_UsesPreviousMainBuildingSlotAfterCompleteScanConfirmsItEmpty()
    {
        var previous = CompleteStatus(new Building(25, "Main Building", 8, "/build.php?id=25", 15));
        var live = CompleteStatus();

        var plan = Assert.IsType<MainBuildingRebuildPlan>(
            MainBuildingRebuildPlanner.Plan(live, previous, [], targetLevel: 7));

        Assert.Equal(25, plan.SlotId);
        Assert.Equal(7, plan.TargetLevel);
    }

    [Fact]
    public void Plan_DoesNothingWhileMainBuildingExists()
    {
        var live = CompleteStatus(new Building(25, "Main Building", 1, "/build.php?id=25", 15));

        Assert.Null(MainBuildingRebuildPlanner.Plan(live, null, [], targetLevel: 1));
    }

    [Fact]
    public void Plan_DoesNothingForPartialDorf2Read()
    {
        var partial = Status(new Building(19, "Empty", 0, "/build.php?id=19"));

        Assert.Null(MainBuildingRebuildPlanner.Plan(partial, null, [], targetLevel: 1));
    }

    [Fact]
    public void Plan_DoesNotDuplicateActiveMainBuildingConstruct()
    {
        var queued = Item(new BuildingConstructPayload(25, 15, "Main Building").ToDictionary());

        Assert.Null(MainBuildingRebuildPlanner.Plan(CompleteStatus(), null, [queued], targetLevel: 1));
    }

    private static VillageStatus CompleteStatus(params Building[] occupiedBuildings)
    {
        var occupiedBySlot = occupiedBuildings.ToDictionary(building => building.SlotId!.Value);
        return Status(Enumerable.Range(19, 22)
            .Select(slot => occupiedBySlot.TryGetValue(slot, out var occupied)
                ? occupied
                : new Building(slot, "Empty", 0, $"/build.php?id={slot}"))
            .ToArray());
    }

    private static VillageStatus Status(params Building[] buildings) => new(
        "G1",
        [],
        new Dictionary<string, string>(),
        [],
        buildings,
        []);

    private static QueueItem Item(Dictionary<string, string> payload) => new()
    {
        TaskName = "construct_building",
        Payload = payload,
        Status = QueueStatus.Pending,
    };
}
