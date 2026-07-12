using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionQueueCoverageTests
{
    [Theory]
    [InlineData(8, 10, 10)]
    [InlineData(9, 10, 10)]
    [InlineData(10, 10, 10)]
    [InlineData(11, 10, null)]
    public void ActiveTravianUpgradeCoversSameSlotTargets(int target, int activeLevel, int? expected)
    {
        var result = ConstructionQueueCoverage.ResolveActiveCoveredLevel(
            CreateItem(29, target),
            CreateStatus(fromOverview: true, new ActiveConstruction(
                ConstructionKind.Building, "Stable", activeLevel, 600, null, SlotId: 29, Gid: 20)));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ActiveLevelElevenCoversAllLowerProgramTargets()
    {
        var status = CreateStatus(fromOverview: true, new ActiveConstruction(
            ConstructionKind.Building, "Stable", 11, 600, null, SlotId: 29, Gid: 20));

        for (var target = 1; target <= 10; target++)
        {
            Assert.Equal(11, ConstructionQueueCoverage.ResolveActiveCoveredLevel(CreateItem(29, target), status));
        }
    }

    [Theory]
    [InlineData(false, 29)]
    [InlineData(true, 30)]
    public void DoesNotPruneFromUntrustedOrDifferentSlotSnapshot(bool fromOverview, int activeSlot)
    {
        var status = CreateStatus(fromOverview, new ActiveConstruction(
            ConstructionKind.Building, "Stable", 10, 600, null, SlotId: activeSlot, Gid: 20));

        Assert.Null(ConstructionQueueCoverage.ResolveActiveCoveredLevel(CreateItem(29, 9), status));
    }

    [Fact]
    public void ResourceConstructionDoesNotCoverBuildingUpgrade()
    {
        var status = CreateStatus(fromOverview: true, new ActiveConstruction(
            ConstructionKind.Resource, "Woodcutter", 10, 600, null, SlotId: 29, Gid: 1));

        Assert.Null(ConstructionQueueCoverage.ResolveActiveCoveredLevel(CreateItem(29, 9), status));
    }

    private static QueueItem CreateItem(int slotId, int targetLevel) => new()
    {
        TaskName = "upgrade_building_to_level",
        Status = QueueStatus.Pending,
        Payload = new Dictionary<string, string>
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = targetLevel.ToString(),
        },
    };

    private static VillageStatus CreateStatus(bool fromOverview, params ActiveConstruction[] active) => new(
        ActiveVillage: "pha",
        Villages: [],
        Resources: new Dictionary<string, string>(),
        ResourceFields: [],
        Buildings: [new Building(29, "Stable", 8, null, 20)],
        BuildQueue: [],
        ActiveConstructions: active,
        ActiveConstructionsFromOverview: fromOverview);
}
