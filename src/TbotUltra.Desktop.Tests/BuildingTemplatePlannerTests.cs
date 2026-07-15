using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingTemplatePlannerTests
{
    private readonly BuildingTemplatePlanner _planner = new();

    [Fact]
    public void Plan_StableBeforePrerequisites_ReturnsError()
    {
        var status = Status("Teutons", Building(19, "Main Building", 1, 15));
        var result = _planner.Plan(
            [Row(20, "Stable", 1)],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, item => item.Contains("Academy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_PrerequisitesBeforeStable_IsAccepted()
    {
        var status = Status("Teutons", Building(19, "Main Building", 1, 15));
        var result = _planner.Plan(
            [
                Row(15, "Main Building", 3),
                Row(16, "Rally Point", 1, preferredSlot: 39),
                Row(19, "Barracks", 3),
                Row(22, "Academy", 5),
                Row(13, "Smithy", 3),
                Row(20, "Stable", 1),
            ],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Actions, item => item.DisplayName.Contains("Stable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_AllResourcesSatisfiesResourceRequirement()
    {
        var status = Status("Teutons", Building(19, "Main Building", 5, 15));
        var result = _planner.Plan(
            [
                AllResources(10),
                Row(5, "Sawmill", 1),
            ],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 5);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Actions, item => item.TaskName == "upgrade_all_resources_to_level");
        Assert.Contains(result.Actions, item => item.DisplayName.Contains("Sawmill", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ResourceScopeWood_QueuesOnlyWoodcutters()
    {
        var status = Status("Teutons");
        var result = _planner.Plan(
            [AllResources(3, "wood")],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.Empty(result.Errors);
        Assert.All(result.Actions, action => Assert.Equal("upgrade_resource_to_level", action.TaskName));
        Assert.All(result.Actions, action => Assert.Contains("Woodcutter", action.DisplayName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Actions, action => action.DisplayName.Contains("Clay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ResourceScopeWood_SatisfiesWoodRequirementOnly()
    {
        var status = Status("Teutons", Building(19, "Main Building", 5, 15));
        var result = _planner.Plan(
            [
                AllResources(10, "wood"),
                Row(5, "Sawmill", 1),
                Row(6, "Brickyard", 1),
            ],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 5);

        Assert.Contains(result.Actions, item => item.DisplayName.Contains("Sawmill", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, item => item.Contains("Clay Pit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ExistingBuildingOnDifferentSlot_ReusesExistingSlot()
    {
        var status = Status(
            "Teutons",
            Building(19, "Main Building", 3, 15),
            Building(39, "Rally Point", 1, 16),
            Building(22, "Barracks", 3, 19));
        var result = _planner.Plan(
            [Row(19, "Barracks", 5, preferredSlot: 30)],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 3);

        Assert.Empty(result.Errors);
        var upgrade = Assert.Single(result.Actions);
        Assert.Equal("upgrade_building_to_level", upgrade.TaskName);
        Assert.Equal(22, upgrade.SlotId);
    }

    [Fact]
    public void Plan_PreferredSlotCollision_ShiftsAndSkipsOnlyUnplaceableRow()
    {
        var occupied = Enumerable.Range(19, 19)
            .Select(slot => Building(slot, $"Cranny {slot}", 10, 23))
            .Prepend(Building(39, "Rally Point", 1, 16))
            .Append(Building(40, "Earth Wall", 1, 32))
            .ToArray();
        var status = Status("Teutons", occupied);
        var result = _planner.Plan(
            [
                Row(10, "Warehouse", 1, preferredSlot: 19),
                Row(11, "Granary", 1, preferredSlot: 19),
            ],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.Empty(result.Errors);
        Assert.Single(result.Actions);
        Assert.Equal(38, result.Actions[0].SlotId);
        Assert.Contains(result.Warnings, item => item.Contains("Granary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_WarehouseQueuedAtLevel0_ReturnsDuplicateError()
    {
        var status = Status("Teutons", Building(19, "Warehouse", 0, 10));
        var result = _planner.Plan(
            [Row(10, "Warehouse", 1, preferredSlot: 20)],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.Contains(result.Errors, item => item.Contains("level 20", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ResidenceQueuedAtLevel0_BlocksPalace()
    {
        var status = Status(
            "Teutons",
            Building(19, "Residence", 0, 25),
            Building(20, "Main Building", 5, 15),
            Building(21, "Embassy", 1, 18));
        var result = _planner.Plan(
            [Row(26, "Palace", 1, preferredSlot: 22)],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 5);

        Assert.Contains(result.Errors, item => item.Contains("conflicts with Residence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_WallRow_MapsToTargetTribeWall()
    {
        var status = Status("Teutons");
        var result = _planner.Plan(
            [Row(31, "City Wall", 1, preferredSlot: 40)],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.Empty(result.Errors);
        var action = Assert.Single(result.Actions);
        Assert.Equal(32, action.Gid);
        Assert.Contains("Earth Wall", action.DisplayName);
    }

    [Fact]
    public void Plan_UnsupportedTribeSpecificBuilding_IsRejected()
    {
        var status = Status("Gauls");
        var result = _planner.Plan(
            [Row(35, "Brewery", 1)],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Actions);
        Assert.Contains(result.Warnings, item => item.Contains("Brewery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_AllResourcesAlreadyAtTarget_DoesNotQueueNoOp()
    {
        var fields = ResourceFields()
            .Select(field => field with { Level = 5 })
            .ToList();
        var status = Status("Teutons") with { ResourceFields = fields };

        var result = _planner.Plan([AllResources(5)], status, 1, 1);

        Assert.Empty(result.Actions);
        Assert.Contains(result.Warnings, item => item.Contains("already meet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ConstructAndUpgradeShareStableTemplateStepId()
    {
        var row = Row(10, "Warehouse", 5);
        var result = _planner.Plan([row], Status("Teutons"), 1, 1);
        var construct = Assert.Single(result.Actions, item => item.TaskName == "construct_building");
        var upgrade = Assert.Single(result.Actions, item => item.TaskName == "upgrade_building_to_level");

        Assert.True(Guid.TryParseExact(
            construct.Payload[BotOptionPayloadKeys.BuildingTemplateStepId],
            "N",
            out _));
        Assert.Equal(
            construct.Payload[BotOptionPayloadKeys.BuildingTemplateStepId],
            upgrade.Payload[BotOptionPayloadKeys.BuildingTemplateStepId]);

        var secondPlan = _planner.Plan([row], Status("Teutons"), 1, 1);
        var secondConstruct = Assert.Single(secondPlan.Actions, item => item.TaskName == "construct_building");
        Assert.NotEqual(
            construct.Payload[BotOptionPayloadKeys.BuildingTemplateStepId],
            secondConstruct.Payload[BotOptionPayloadKeys.BuildingTemplateStepId]);
    }

    [Fact]
    public void EvaluateBuildingAvailability_DistinguishesAvailableMissingRequirementsAndWrongTribe()
    {
        var status = Status("Teutons", Building(30, "Main Building", 1, 15));

        var available = _planner.EvaluateBuildingAvailability(23, [], status, 1, 1);
        var missingRequirements = _planner.EvaluateBuildingAvailability(20, [], status, 1, 1);
        var wrongTribe = _planner.EvaluateBuildingAvailability(41, [], status, 1, 1);

        Assert.Equal(BuildingTemplateAvailability.Available, available.Availability);
        Assert.Equal(BuildingTemplateAvailability.MissingRequirements, missingRequirements.Availability);
        Assert.Equal(BuildingTemplateAvailability.Unavailable, wrongTribe.Availability);
    }

    [Fact]
    public void EvaluateBuildingAvailability_CountsOnlyEarlierTemplatePrerequisites()
    {
        var status = Status("Teutons", Building(30, "Main Building", 3, 15));
        var precedingRows = new[]
        {
            Row(16, "Rally Point", 1, preferredSlot: 39),
            Row(19, "Barracks", 3),
            Row(22, "Academy", 5),
            Row(13, "Smithy", 3),
        };

        var result = _planner.EvaluateBuildingAvailability(20, precedingRows, status, 1, 3);

        Assert.Equal(BuildingTemplateAvailability.Available, result.Availability);
    }

    [Fact]
    public void Plan_AutoSlot_DoesNotConsumeLaterExplicitSlot()
    {
        var status = Status("Teutons", Building(30, "Main Building", 1, 15));
        var result = _planner.Plan(
            [
                Row(10, "Warehouse", 1),
                Row(11, "Granary", 1, preferredSlot: 19),
            ],
            status,
            serverSpeed: 1,
            mainBuildingLevel: 1);

        Assert.Empty(result.Errors);
        var constructs = result.Actions.Where(item => item.TaskName == "construct_building").ToList();
        Assert.Equal(20, constructs[0].SlotId);
        Assert.Equal(19, constructs[1].SlotId);
        Assert.Equal("19", constructs[0].Payload[BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots]);
        Assert.Equal(bool.TrueString, constructs[0].Payload[BotOptionPayloadKeys.BuildingConstructAllowSlotFallback]);
    }

    [Fact]
    public void PlanMissingPrerequisites_ProducesBuildableStableChainInDependencyOrder()
    {
        var status = Status("Teutons", Building(30, "Main Building", 1, 15));

        var prerequisites = _planner.PlanMissingPrerequisites(20, [], status, 1, 1, reservedSlotId: 31);
        var rows = prerequisites.Rows.Append(Row(20, "Stable", 1, preferredSlot: 31)).ToList();
        var result = _planner.Plan(rows, status, 1, 1);

        Assert.Empty(prerequisites.Blockers);
        Assert.NotEmpty(prerequisites.Rows);
        Assert.Empty(result.Errors);
        Assert.Equal(31, result.Actions.Last(item => item.DisplayName.Contains("Stable", StringComparison.OrdinalIgnoreCase)).SlotId);
        var prerequisiteRows = prerequisites.Rows.ToList();
        Assert.True(
            prerequisiteRows.FindIndex(item => item.Gid == 19)
            < prerequisiteRows.FindIndex(item => item.Gid == 22));
    }

    [Fact]
    public void PlanMissingPrerequisites_DoesNotAddAlreadySatisfiedRows()
    {
        var status = Status(
            "Teutons",
            Building(30, "Main Building", 3, 15),
            Building(39, "Rally Point", 1, 16),
            Building(19, "Barracks", 3, 19),
            Building(20, "Academy", 5, 22),
            Building(21, "Smithy", 3, 13));

        var prerequisites = _planner.PlanMissingPrerequisites(20, [], status, 1, 3);

        Assert.Empty(prerequisites.Blockers);
        Assert.Empty(prerequisites.Rows);
    }

    [Fact]
    public void PlanMissingPrerequisites_IncludesRequiredResourceUpgrades()
    {
        var status = Status("Teutons", Building(30, "Main Building", 1, 15));

        var prerequisites = _planner.PlanMissingPrerequisites(5, [], status, 1, 1);
        var result = _planner.Plan(
            prerequisites.Rows.Append(Row(5, "Sawmill", 1)).ToList(),
            status,
            1,
            1);

        Assert.Empty(prerequisites.Blockers);
        Assert.Contains(
            prerequisites.Rows,
            item => item.Kind == BuildingTemplateRowKind.AllResources
                && item.ResourceScope == "wood"
                && item.TargetLevel >= 10);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RowView_MissingRequirementOption_IsInvokableButNotDirectlySelectable()
    {
        var available = new BuildingTemplateTargetOption(23, "Cranny", "Infrastructure", null, null);
        var missing = new BuildingTemplateTargetOption(
            20,
            "Stable",
            "Military",
            null,
            null,
            BuildingTemplateAvailability.MissingRequirements,
            "Requires Academy 5+.");
        var row = new BuildingTemplateRowView { Target = available };

        row.Target = missing;

        Assert.Same(available, row.Target);
        Assert.True(missing.CanInvoke);
        Assert.False(missing.IsSelectable);
    }

    [Fact]
    public void RowView_SlotOptions_KeepSpecialSlotsFixedAndOrdinarySlotsValid()
    {
        var rallyPoint = new BuildingTemplateTargetOption(16, "Rally Point", "Military", null, 39);
        var warehouse = new BuildingTemplateTargetOption(10, "Warehouse", "Infrastructure", null, null);
        var row = new BuildingTemplateRowView { Target = rallyPoint };

        Assert.Equal("39", row.SlotText);
        Assert.Equal(["39"], row.SlotOptions);
        Assert.False(row.IsSlotSelectable);

        row.Target = warehouse;

        Assert.Equal("Auto", row.SlotText);
        Assert.DoesNotContain("39", row.SlotOptions);
        Assert.DoesNotContain("40", row.SlotOptions);
        Assert.True(row.IsSlotSelectable);
    }

    private static BuildingTemplateRow Row(int gid, string name, int level, int? preferredSlot = null)
        => new()
        {
            Kind = BuildingTemplateRowKind.Building,
            Gid = gid,
            BuildingName = name,
            TargetLevel = level,
            PreferredSlotId = preferredSlot,
        };

    private static BuildingTemplateRow AllResources(int level, string scope = "all")
        => new()
        {
            Kind = BuildingTemplateRowKind.AllResources,
            TargetLevel = level,
            ResourceScope = scope,
        };

    private static Building Building(int slot, string name, int level, int gid)
        => new(slot, name, level, null, gid);

    private static VillageStatus Status(string tribe, params Building[] buildings)
        => new(
            ActiveVillage: "1440",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: ResourceFields(),
            Buildings: buildings,
            BuildQueue: [],
            Tribe: tribe,
            IsCapital: false);

    private static IReadOnlyList<ResourceField> ResourceFields()
    {
        var fields = new List<ResourceField>();
        for (var slot = 1; slot <= 18; slot++)
        {
            var name = (slot % 4) switch
            {
                1 => "Woodcutter",
                2 => "Clay Pit",
                3 => "Iron Mine",
                _ => "Cropland",
            };
            fields.Add(new ResourceField(slot, name, name, 0, null));
        }

        return fields;
    }
}
