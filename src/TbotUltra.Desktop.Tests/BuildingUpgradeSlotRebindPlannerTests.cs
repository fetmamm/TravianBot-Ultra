using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingUpgradeSlotRebindPlannerTests
{
    [Fact]
    public void ConstructionQueueReconciliation_RemovesStaleConstructAndRebindsDependentUpgrade()
    {
        var construct = Item("construct_building", new BuildingConstructPayload(38, 22, "Academy").ToDictionary());
        var upgrade = Item("upgrade_building_to_level", new BuildingUpgradePayload(38, 5, "Academy").ToDictionary());

        var plan = ConstructionQueueReconciliation.Plan(
            Status(new Building(37, "Academy", 3, "/build.php?id=37", 22)),
            [construct, upgrade]);

        Assert.Contains(construct.Id, plan.Removals);
        var update = Assert.Single(plan.Updates);
        Assert.Equal(upgrade.Id, update.QueueItemId);
        Assert.Equal("37", update.Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
    }

    [Fact]
    public void ConstructionQueueReconciliation_LeavesInputPayloadsUnchanged()
    {
        var construct = Item("construct_building", new BuildingConstructPayload(38, 22, "Academy").ToDictionary());
        var upgrade = Item("upgrade_building_to_level", new BuildingUpgradePayload(38, 5, "Academy").ToDictionary());

        var plan = ConstructionQueueReconciliation.Plan(
            Status(new Building(37, "Academy", 3, "/build.php?id=37", 22)),
            [construct, upgrade]);

        Assert.Contains(construct.Id, plan.Removals);
        Assert.Equal("38", upgrade.Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
        Assert.Equal("37", Assert.Single(plan.Updates).Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
    }

    [Fact]
    public void Plan_RebindsAcademyUpgradesWhenLiveDuplicateIsInAnotherSlot()
    {
        var source = Item(
            "construct_building",
            new BuildingConstructPayload(38, 22, "Academy").ToDictionary());
        var academyLevel5 = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(38, 5, "Academy").ToDictionary());
        var academyMax = Item(
            "upgrade_building_to_max",
            new BuildingUpgradePayload(38, null, "Academy").ToDictionary());
        var otherBuilding = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(38, 5, "Smithy").ToDictionary());

        var result = BuildingUpgradeSlotRebindPlanner.Plan(
            source,
            effectiveSlotId: 37,
            [academyLevel5, academyMax, otherBuilding]);

        Assert.Equal(2, result.Count);
        Assert.All(result, rebind =>
            Assert.Equal("37", rebind.Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]));
        Assert.DoesNotContain(result, rebind => rebind.QueueItemId == otherBuilding.Id);
    }

    [Fact]
    public void Plan_DoesNotRebindDuplicateAllowedBuildingFamily()
    {
        var source = Item(
            "construct_building",
            new BuildingConstructPayload(38, 10, "Warehouse").ToDictionary());
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(38, 5, "Warehouse").ToDictionary());

        Assert.Empty(BuildingUpgradeSlotRebindPlanner.Plan(source, 37, [upgrade]));
    }

    [Fact]
    public void PlanFromLiveStatus_RemovesWrongSlotAcademyUpgradeWhenTargetAlreadyMet()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(38, 5, "Academy").ToDictionary());
        var status = Status(new Building(37, "Academy", 5, "/build.php?id=37", 22));

        var reconciliation = Assert.Single(
            BuildingUpgradeSlotRebindPlanner.PlanFromLiveStatus(status, [upgrade]));

        Assert.True(reconciliation.TargetSatisfied);
        Assert.Equal(38, reconciliation.QueuedSlotId);
        Assert.Equal(37, reconciliation.LiveSlotId);
        Assert.Equal(5, reconciliation.LiveLevel);
    }

    [Fact]
    public void PlanFromLiveStatus_RebindsWrongSlotAcademyUpgradeWhenTargetNotMet()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(38, 5, "Academy").ToDictionary());
        var status = Status(new Building(37, "Academy", 3, "/build.php?id=37", 22));

        var reconciliation = Assert.Single(
            BuildingUpgradeSlotRebindPlanner.PlanFromLiveStatus(status, [upgrade]));

        Assert.False(reconciliation.TargetSatisfied);
        Assert.Equal("37", reconciliation.Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
    }

    [Fact]
    public void FindExistingConstruct_FindsAcademyBeforeDesktopQueueDelay()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(38, 22, "Academy").ToDictionary());
        var status = Status(new Building(37, "Academy", 5, "/build.php?id=37", 22));

        var match = Assert.IsType<BuildingConstructLiveMatch>(
            BuildingUpgradeSlotRebindPlanner.FindExistingConstruct(status, construct));

        Assert.Equal(38, match.QueuedSlotId);
        Assert.Equal(37, match.LiveSlotId);
        Assert.Equal(5, match.LiveLevel);
    }

    [Fact]
    public void PlanUpgradeFromLiveStatus_ReportsSameSlotIdentityForMissingBuildingRecovery()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(37, 10, "Academy").ToDictionary());
        var status = Status(new Building(37, "Academy", 5, "/build.php?id=37", 22));

        var reconciliation = Assert.IsType<BuildingUpgradeLiveReconciliation>(
            BuildingUpgradeSlotRebindPlanner.PlanUpgradeFromLiveStatus(status, upgrade));

        Assert.False(reconciliation.TargetSatisfied);
        Assert.Equal(reconciliation.QueuedSlotId, reconciliation.LiveSlotId);
        Assert.Empty(BuildingUpgradeSlotRebindPlanner.PlanFromLiveStatus(status, [upgrade]));
    }

    [Fact]
    public void RepairSafety_DetectsIncompleteOverviewAndUnknownLevelIdentity()
    {
        var incomplete = Status(new Building(37, "Academy", null, "/build.php?id=37", 22));
        var complete = Status(Enumerable.Range(19, 22)
            .Select(slot => slot == 37
                ? new Building(slot, "Academy", null, $"/build.php?id={slot}", 22)
                : new Building(slot, "Empty", 0, $"/build.php?id={slot}"))
            .ToArray());

        Assert.False(BuildingUpgradeSlotRebindPlanner.HasCompleteBuildingOverview(incomplete));
        Assert.True(BuildingUpgradeSlotRebindPlanner.HasCompleteBuildingOverview(complete));
        Assert.True(BuildingUpgradeSlotRebindPlanner.HasLiveBuildingIdentity(complete, 22));
    }

    private static VillageStatus Status(params Building[] buildings) => new(
        "G1",
        [],
        new Dictionary<string, string>(),
        [],
        buildings,
        []);

    private static QueueItem Item(string taskName, Dictionary<string, string> payload) => new()
    {
        TaskName = taskName,
        Payload = payload,
        Status = QueueStatus.Pending,
    };
}
