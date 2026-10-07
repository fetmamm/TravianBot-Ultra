using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingConstructionProjectionTests
{
    [Fact]
    public void ExternalUpgradeTargets_ProjectsTwoQueuedWaterworksLevels()
    {
        IReadOnlyList<ActiveConstruction> activeConstructions =
        [
            new(ConstructionKind.Building, "Waterworks", 4, 246, "done at 22:15"),
            new(ConstructionKind.Building, "Waterworks", 5, 756, "done at 22:23"),
        ];

        var targets = MainWindow.BuildExternalUpgradeTargetsBySlot(
            activeConstructions,
            ConstructionKind.Building,
            [(43, "Waterworks", 3)]);

        Assert.Equal(5, targets[43]);
    }

    [Fact]
    public void ExternalUpgradeTargets_DoesNotGuessBetweenDuplicateBuildings()
    {
        IReadOnlyList<ActiveConstruction> activeConstructions =
        [
            new(ConstructionKind.Building, "Warehouse", 4, 246, "done at 22:15"),
        ];

        var targets = MainWindow.BuildExternalUpgradeTargetsBySlot(
            activeConstructions,
            ConstructionKind.Building,
            [(19, "Warehouse", 3), (34, "Warehouse", 3)]);

        Assert.Empty(targets);
    }
}
