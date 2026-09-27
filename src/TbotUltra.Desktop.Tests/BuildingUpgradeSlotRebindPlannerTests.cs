using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingUpgradeSlotRebindPlannerTests
{
    [Fact]
    public void ConstructionQueueReconciliation_RemovesDeferredResourceUpgradeWhenTargetIsReached()
    {
        var upgrade = Item(
            "upgrade_resource_to_level",
            new ResourceUpgradePayload(4, 6, "Iron Mine").ToDictionary());
        upgrade.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1);
        var status = ResourceStatus(new ResourceField(4, "Iron Mine", "Iron Mine", 6, "/build.php?id=4"));

        var plan = ConstructionQueueReconciliation.Plan(status, [upgrade]);

        Assert.Contains(upgrade.Id, plan.Removals);
    }

    [Fact]
    public void ConstructionQueueReconciliation_PreservesResourceUpgradeBelowTarget()
    {
        var upgrade = Item(
            "upgrade_resource_to_level",
            new ResourceUpgradePayload(4, 6, "Iron Mine").ToDictionary());
        var status = ResourceStatus(new ResourceField(4, "Iron Mine", "Iron Mine", 5, "/build.php?id=4"));

        var plan = ConstructionQueueReconciliation.Plan(status, [upgrade]);

        Assert.DoesNotContain(upgrade.Id, plan.Removals);
    }

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
    public void ConstructionQueueReconciliation_KeepsCompositeConstructUntilFinalTarget()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(29, 23, "Cranny", 10).ToDictionary());

        var belowTarget = ConstructionQueueReconciliation.Plan(
            Status(new Building(29, "Cranny", 1, "/build.php?id=29", 23)),
            [construct]);
        var atTarget = ConstructionQueueReconciliation.Plan(
            Status(new Building(29, "Cranny", 10, "/build.php?id=29", 23)),
            [construct]);

        Assert.False(belowTarget.HasChanges);
        Assert.Contains(construct.Id, atTarget.Removals);
    }

    [Fact]
    public void ConstructionQueueReconciliation_RebindsCompositeConstructWithoutRemovingIt()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(38, 22, "Academy", 5).ToDictionary());

        var plan = ConstructionQueueReconciliation.Plan(
            Status(new Building(37, "Academy", 1, "/build.php?id=37", 22)),
            [construct]);

        Assert.Empty(plan.Removals);
        var update = Assert.Single(plan.Updates);
        Assert.Equal(construct.Id, update.QueueItemId);
        Assert.Equal("37", update.Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
        Assert.Equal("5", update.Payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
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
    public void ConstructionQueueReconciliation_PreservesWarehouseConstructBeforeLevel20()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(38, 10, "Warehouse").ToDictionary());
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(38, 5, "Warehouse").ToDictionary());

        var plan = ConstructionQueueReconciliation.Plan(
            Status(new Building(37, "Warehouse", 12, "/build.php?id=37", 10)),
            [construct, upgrade]);

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void ConstructionQueueReconciliation_PreservesWarehouseConstructAfterLevel20()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(38, 10, "Warehouse").ToDictionary());

        var plan = ConstructionQueueReconciliation.Plan(
            Status(new Building(37, "Warehouse", 20, "/build.php?id=37", 10)),
            [construct]);

        Assert.False(plan.HasChanges);
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
    public void ConstructionQueueReconciliation_RemovesSatisfiedWarehouseUpgradeInExactSlot()
    {
        var payload = new BuildingUpgradePayload(19, 6, "Warehouse").ToDictionary();
        payload[BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonInProgress;
        var upgrade = Item("upgrade_building_to_level", payload);
        var status = Status(new Building(19, "Warehouse", 6, "/build.php?id=19", 10));

        var plan = ConstructionQueueReconciliation.Plan(status, [upgrade]);

        Assert.Contains(upgrade.Id, plan.Removals);
    }

    [Fact]
    public void PlanFromLiveStatus_DoesNotRebindWarehouseUpgradeToDifferentDuplicateSlot()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(19, 6, "Warehouse").ToDictionary());
        var status = Status(new Building(20, "Warehouse", 6, "/build.php?id=20", 10));

        var reconciliation = BuildingUpgradeSlotRebindPlanner.PlanUpgradeFromLiveStatus(status, upgrade);

        Assert.Null(reconciliation);
    }

    [Fact]
    public void PlanFromLiveStatus_RebindsDuplicateBuildingWhenExactlyOneInstanceStillNeedsTarget()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(35, 20, "Granary").ToDictionary());
        var status = Status(
            new Building(24, "Granary", 20, "/build.php?id=24", 11),
            new Building(35, "Trade Office", 10, "/build.php?id=35", 28),
            new Building(36, "Granary", 6, "/build.php?id=36", 11));

        var reconciliation = Assert.Single(
            BuildingUpgradeSlotRebindPlanner.PlanFromLiveStatus(status, [upgrade]));

        Assert.False(reconciliation.TargetSatisfied);
        Assert.Equal(35, reconciliation.QueuedSlotId);
        Assert.Equal(36, reconciliation.LiveSlotId);
        Assert.Equal("36", reconciliation.Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
    }

    [Fact]
    public void PlanFromLiveStatus_DoesNotGuessBetweenMultipleDuplicateBuildingsBelowTarget()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(35, 20, "Granary").ToDictionary());
        var status = Status(
            new Building(35, "Trade Office", 10, "/build.php?id=35", 28),
            new Building(36, "Granary", 6, "/build.php?id=36", 11),
            new Building(37, "Granary", 8, "/build.php?id=37", 11));

        Assert.Empty(BuildingUpgradeSlotRebindPlanner.PlanFromLiveStatus(status, [upgrade]));
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
    public void FindExistingConstruct_FindsProductionBuildingMovedToAnotherSlot()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(27, 6, "Brickyard").ToDictionary());
        var status = Status(
            new Building(27, "Sawmill", 1, "/build.php?id=27", 5),
            new Building(28, "Brickyard", 2, "/build.php?id=28", 6));

        var match = Assert.IsType<BuildingConstructLiveMatch>(
            BuildingUpgradeSlotRebindPlanner.FindExistingConstruct(status, construct));

        Assert.Equal(27, match.QueuedSlotId);
        Assert.Equal(28, match.LiveSlotId);
        Assert.Equal(2, match.LiveLevel);
    }

    [Fact]
    public void ConstructionQueueReconciliation_PreservesConditionalDuplicateUntilItsOwnSlotExists()
    {
        var firstCranny = new Building(28, "Cranny", 6, "/build.php?id=28", 23);
        var firstConstruct = Item(
            "construct_building",
            new BuildingConstructPayload(29, 23, "Cranny").ToDictionary());
        var firstUpgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(29, 10, "Cranny").ToDictionary());
        var secondConstruct = Item(
            "construct_building",
            new BuildingConstructPayload(30, 23, "Cranny").ToDictionary());
        var secondUpgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(30, 10, "Cranny").ToDictionary());

        var plan = ConstructionQueueReconciliation.Plan(
            Status(firstCranny),
            [firstConstruct, firstUpgrade, secondConstruct, secondUpgrade]);

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void ConstructionQueueReconciliation_RebindsOccupiedConstructAndDependentUpgradeToFreeSlot()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(25, 22, "Academy").ToDictionary());
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(25, 5, "Academy").ToDictionary());
        var status = CompleteStatus(new Building(25, "Cranny", 1, "/build.php?id=25", 23));

        var plan = ConstructionQueueReconciliation.Plan(status, [construct, upgrade]);

        Assert.Empty(plan.Removals);
        Assert.Equal(2, plan.Updates.Count);
        Assert.Equal(
            "19",
            Assert.Single(plan.Updates, update => update.QueueItemId == construct.Id)
                .Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
        Assert.Equal(
            "19",
            Assert.Single(plan.Updates, update => update.QueueItemId == upgrade.Id)
                .Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
    }

    [Fact]
    public void ConstructionQueueReconciliation_DoesNotRebindOccupiedConstructFromIncompleteOverview()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(25, 22, "Academy").ToDictionary());
        var status = Status(new Building(25, "Cranny", 1, "/build.php?id=25", 23));

        var plan = ConstructionQueueReconciliation.Plan(status, [construct]);

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void ConstructionQueueReconciliation_DoesNotStealAnotherQueuedConstructSlot()
    {
        var conflicted = Item(
            "construct_building",
            new BuildingConstructPayload(25, 22, "Academy").ToDictionary());
        var reserved = Item(
            "construct_building",
            new BuildingConstructPayload(19, 13, "Smithy").ToDictionary());
        var status = CompleteStatus(new Building(25, "Cranny", 1, "/build.php?id=25", 23));

        var plan = ConstructionQueueReconciliation.Plan(status, [conflicted, reserved]);

        var update = Assert.Single(plan.Updates);
        Assert.Equal(conflicted.Id, update.QueueItemId);
        Assert.Equal("20", update.Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
    }

    [Fact]
    public void PlanConstructSlotConflict_RebindsCurrentRunningItemForPreExecutionSafety()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(25, 22, "Academy").ToDictionary());
        construct.Status = QueueStatus.Running;
        var status = CompleteStatus(new Building(25, "Cranny", 1, "/build.php?id=25", 23));

        var conflict = Assert.IsType<BuildingConstructSlotConflictReconciliation>(
            BuildingUpgradeSlotRebindPlanner.PlanConstructSlotConflict(status, construct, [construct]));

        Assert.Equal(19, conflict.ReboundSlotId);
        Assert.Equal(construct.Id, Assert.Single(conflict.Updates).QueueItemId);
    }

    [Fact]
    public void PlanConstructSlotConflict_KeepsItemWhenNoSafeFreeSlotExists()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(25, 22, "Academy").ToDictionary());
        var status = Status(Enumerable.Range(19, 22)
            .Select(slot => new Building(slot, "Cranny", 1, $"/build.php?id={slot}", 23))
            .ToArray());

        var conflict = Assert.IsType<BuildingConstructSlotConflictReconciliation>(
            BuildingUpgradeSlotRebindPlanner.PlanConstructSlotConflict(status, construct, [construct]));

        Assert.Null(conflict.ReboundSlotId);
        Assert.Empty(conflict.ConfirmedEmptySlotIds);
        Assert.Empty(conflict.Updates);
    }

    [Fact]
    public void ConstructionQueueReconciliation_AssignsUniqueSlotsToMultipleConflictsInQueueOrder()
    {
        var first = Item(
            "construct_building",
            new BuildingConstructPayload(25, 5, "Sawmill").ToDictionary());
        var second = Item(
            "construct_building",
            new BuildingConstructPayload(26, 6, "Brickyard").ToDictionary());
        first.Priority = 10;
        second.Priority = 5;
        var status = CompleteStatus(
            new Building(25, "Cranny", 1, "/build.php?id=25", 23),
            new Building(26, "Cranny", 1, "/build.php?id=26", 23));

        var plan = ConstructionQueueReconciliation.Plan(status, [second, first]);

        Assert.Equal("19", Assert.Single(plan.Updates, update => update.QueueItemId == first.Id)
            .Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
        Assert.Equal("20", Assert.Single(plan.Updates, update => update.QueueItemId == second.Id)
            .Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
    }

    [Fact]
    public void PlanConstructSlotConflict_HonorsTemplateFallbackExclusions()
    {
        var payload = new BuildingConstructPayload(25, 22, "Academy").ToDictionary();
        payload[BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots] = "19";
        var construct = Item("construct_building", payload);
        var status = CompleteStatus(new Building(25, "Cranny", 1, "/build.php?id=25", 23));

        var conflict = Assert.IsType<BuildingConstructSlotConflictReconciliation>(
            BuildingUpgradeSlotRebindPlanner.PlanConstructSlotConflict(status, construct, [construct]));

        Assert.Equal(20, conflict.ReboundSlotId);
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

    private static VillageStatus ResourceStatus(params ResourceField[] fields) => new(
        "G1",
        [],
        new Dictionary<string, string>(),
        fields,
        [],
        []);

    private static VillageStatus CompleteStatus(params Building[] occupiedBuildings)
    {
        var occupiedBySlot = occupiedBuildings.ToDictionary(building => building.SlotId!.Value);
        return Status(Enumerable.Range(19, 22)
            .Select(slot => occupiedBySlot.TryGetValue(slot, out var occupied)
                ? occupied
                : new Building(slot, "Empty", 0, $"/build.php?id={slot}"))
            .ToArray());
    }

    private static QueueItem Item(string taskName, Dictionary<string, string> payload) => new()
    {
        TaskName = taskName,
        Payload = payload,
        Status = QueueStatus.Pending,
    };
}
