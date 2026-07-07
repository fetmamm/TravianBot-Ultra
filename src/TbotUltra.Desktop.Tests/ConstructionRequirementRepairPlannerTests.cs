using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionRequirementRepairPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_StableWithoutAcademy_ConstructsAndUpgradesAcademy()
    {
        var parent = StableConstruct();
        var status = CreateStatus(
        [
            new Building(19, "Main Building", 5, null, 15),
            new Building(20, "Barracks", 3, null, 19),
            new Building(21, "Smithy", 3, null, 13),
            new Building(39, "Rally Point", 1, null, 16),
        ]);

        var plan = ConstructionRequirementRepairPlanner.Plan(parent, status, [], Now);

        Assert.Empty(plan.Blockers);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("construct_building", plan.Steps[0].TaskName);
        Assert.Equal("22", plan.Steps[0].Payload[BotOptionPayloadKeys.BuildingConstructGid]);
        Assert.Equal("upgrade_building_to_level", plan.Steps[1].TaskName);
        Assert.Equal("5", plan.Steps[1].Payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
    }

    [Fact]
    public void Plan_StableWithoutAcademyAndBarracks_RepairsRecursively()
    {
        var parent = StableConstruct();
        var status = CreateStatus(
        [
            new Building(19, "Main Building", 5, null, 15),
            new Building(20, "Smithy", 3, null, 13),
            new Building(39, "Rally Point", 1, null, 16),
        ]);

        var plan = ConstructionRequirementRepairPlanner.Plan(parent, status, [], Now);

        Assert.Empty(plan.Blockers);
        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal("Barracks", plan.Steps[0].Payload[BotOptionPayloadKeys.BuildingConstructName]);
        Assert.Equal("3", plan.Steps[1].Payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
        Assert.Equal("Academy", plan.Steps[2].Payload[BotOptionPayloadKeys.BuildingConstructName]);
        Assert.Equal("5", plan.Steps[3].Payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
    }

    [Fact]
    public void Plan_StableWithLowAcademy_UpgradesAcademyOnly()
    {
        var parent = StableConstruct();
        var status = CreateStatus(
        [
            new Building(19, "Main Building", 5, null, 15),
            new Building(20, "Barracks", 3, null, 19),
            new Building(21, "Academy", 3, null, 22),
            new Building(22, "Smithy", 3, null, 13),
            new Building(39, "Rally Point", 1, null, 16),
        ]);

        var plan = ConstructionRequirementRepairPlanner.Plan(parent, status, [], Now);

        Assert.Empty(plan.Blockers);
        Assert.Single(plan.Steps);
        Assert.Equal("upgrade_building_to_level", plan.Steps[0].TaskName);
        Assert.Equal("21", plan.Steps[0].Payload[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
        Assert.Equal("5", plan.Steps[0].Payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
    }

    [Fact]
    public void Plan_QueuedAcademyRepair_PromotesExistingItems()
    {
        var parent = StableConstruct();
        var status = CreateStatus(
        [
            new Building(19, "Main Building", 5, null, 15),
            new Building(20, "Barracks", 3, null, 19),
            new Building(21, "Smithy", 3, null, 13),
            new Building(39, "Rally Point", 1, null, 16),
        ]);
        var construct = QueueItem(
            "construct_building",
            new BuildingConstructPayload(30, 22, "Academy").ToDictionary());
        var upgrade = QueueItem(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(30, 5, "Academy").ToDictionary());

        var plan = ConstructionRequirementRepairPlanner.Plan(parent, status, [construct, upgrade], Now);

        Assert.Empty(plan.Blockers);
        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps, step => Assert.Equal(ConstructionRequirementRepairStepKind.Promote, step.Kind));
        Assert.Equal(construct.Id, plan.Steps[0].ExistingQueueItemId);
        Assert.Equal(upgrade.Id, plan.Steps[1].ExistingQueueItemId);
    }

    [Fact]
    public void Plan_NoFreeSlot_BlocksRepair()
    {
        var parent = StableConstruct();
        var buildings = Enumerable.Range(19, 20)
            .Select(slot => new Building(slot, $"Building {slot}", 1, null, 23))
            .ToList();
        buildings[0] = new Building(19, "Main Building", 5, null, 15);
        buildings[1] = new Building(20, "Barracks", 3, null, 19);
        buildings[2] = new Building(21, "Smithy", 3, null, 13);
        buildings.Add(new Building(39, "Rally Point", 1, null, 16));

        var plan = ConstructionRequirementRepairPlanner.Plan(parent, CreateStatus(buildings), [], Now);

        Assert.True(plan.HasBlockers);
        Assert.Contains(plan.Blockers, item => item.Contains("no free building slot", StringComparison.OrdinalIgnoreCase));
    }

    private static QueueItem StableConstruct()
        => QueueItem(
            "construct_building",
            new BuildingConstructPayload(29, 20, "Stable").ToDictionary());

    private static QueueItem QueueItem(string taskName, Dictionary<string, string> payload)
        => new()
        {
            Id = Guid.NewGuid(),
            TaskName = taskName,
            Status = QueueStatus.Pending,
            Payload = payload,
            NextAttemptAt = Now,
        };

    private static VillageStatus CreateStatus(IReadOnlyList<Building> buildings)
        => new(
            ActiveVillage: "1660",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: [],
            Tribe: "Teutons",
            VillageCount: 1,
            ActiveConstructions: [],
            ActiveConstructionsFromOverview: true);
}
