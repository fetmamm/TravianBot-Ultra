using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingTemplateMultiVillageTests
{
    [Fact]
    public void Baseline_IsAStandardNewVillageAndDoesNotCopyLiveLevels()
    {
        var baseline = BuildingTemplateBaselineFactory.Create("Romans");

        Assert.Equal(18, baseline.ResourceFields.Count);
        Assert.All(baseline.ResourceFields, field => Assert.Equal(0, field.Level));
        Assert.Equal(4, baseline.ResourceFields.Count(field => field.Name == "Woodcutter"));
        Assert.Equal(4, baseline.ResourceFields.Count(field => field.Name == "Clay Pit"));
        Assert.Equal(4, baseline.ResourceFields.Count(field => field.Name == "Iron Mine"));
        Assert.Equal(6, baseline.ResourceFields.Count(field => field.Name == "Cropland"));
        var mainBuilding = Assert.Single(baseline.Buildings);
        Assert.Equal(15, mainBuilding.Gid);
        Assert.Equal(1, mainBuilding.Level);
    }

    [Fact]
    public void VillageRow_MissingSnapshotIsDisabled()
    {
        var village = Village("New village", enabled: true);
        var target = new BuildingTemplateVillageTarget(village, null, null, []);

        var row = BuildingTemplateVillageQueueRow.Create(target, [], new BuildingTemplatePlanner(), 1);

        Assert.False(row.CanSelect);
        Assert.False(row.IsSelected);
        Assert.Equal("Load buildings first.", row.StatusText);
    }

    [Fact]
    public void VillageRow_AvailableAutoOffVillageIsSelectedWithWaitingWarning()
    {
        var village = Village("Paused village", enabled: false);
        var status = Status("Paused village");
        var rows = new[]
        {
            new BuildingTemplateRow
            {
                Kind = BuildingTemplateRowKind.Building,
                Gid = 23,
                BuildingName = "Cranny",
                TargetLevel = 1,
            },
        };
        var target = new BuildingTemplateVillageTarget(village, status, status, []);

        var row = BuildingTemplateVillageQueueRow.Create(target, rows, new BuildingTemplatePlanner(), 1);

        Assert.True(row.CanSelect);
        Assert.True(row.IsSelected);
        Assert.Contains("Auto off", row.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void VillageRow_AlreadyCompleteVillageIsDisabled()
    {
        var village = Village("Complete", enabled: true);
        var status = Status("Complete", new Building(19, "Cranny", 10, null, 23));
        var rows = new[]
        {
            new BuildingTemplateRow
            {
                Kind = BuildingTemplateRowKind.Building,
                Gid = 23,
                BuildingName = "Cranny",
                TargetLevel = 10,
            },
        };
        var target = new BuildingTemplateVillageTarget(village, status, status, []);

        var row = BuildingTemplateVillageQueueRow.Create(target, rows, new BuildingTemplatePlanner(), 1);

        Assert.False(row.CanSelect);
        Assert.False(row.IsSelected);
        Assert.Contains("Already complete", row.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void VillageRow_RepeatedCranniesWithOneCompleteInstanceQueuesRemainingInstances()
    {
        var village = Village("Expandable", enabled: true);
        var status = Status("Expandable", new Building(19, "Cranny", 10, null, 23));
        var rows = Enumerable.Range(0, 4)
            .Select(_ => new BuildingTemplateRow
            {
                Kind = BuildingTemplateRowKind.Building,
                Gid = 23,
                BuildingName = "Cranny",
                TargetLevel = 10,
            })
            .ToList();
        var target = new BuildingTemplateVillageTarget(village, status, status, []);

        var row = BuildingTemplateVillageQueueRow.Create(target, rows, new BuildingTemplatePlanner(), 1);

        Assert.True(row.CanSelect);
        Assert.True(row.IsSelected);
        Assert.Equal("Ready to queue.", row.StatusText);
        Assert.Equal(3, row.Plan!.Actions.Count(action => action.TaskName == "construct_building"));
    }

    [Fact]
    public void TemplateWindow_ExposesMultiVillageQueueActionAndNewVillageEstimate()
    {
        var root = TbotUltra.Worker.ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));
        var dialogXaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplateVillageQueueWindow.xaml"));

        Assert.Contains("Queue to multiple villages", xaml, StringComparison.Ordinal);
        Assert.Contains("From new village:", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Queue\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Cancel\"", dialogXaml, StringComparison.Ordinal);
    }

    private static VillageSelectionItem Village(string name, bool enabled) => new()
    {
        Name = name,
        Url = $"/dorf1.php?newdid={name.Length}",
        CoordX = name.Length,
        CoordY = -name.Length,
        Tribe = "Romans",
        IsEnabledForAutomation = enabled,
    };

    private static VillageStatus Status(string name, params Building[] occupiedBuildings)
    {
        var fields = BuildingTemplateBaselineFactory.Create("Romans").ResourceFields;
        var buildings = occupiedBuildings
            .Concat([new Building(26, "Main Building", 1, null, 15)])
            .ToList();
        return new VillageStatus(
            name,
            [],
            new Dictionary<string, string>(),
            fields,
            buildings,
            [],
            "Romans",
            IsCapital: false,
            WarehouseCapacity: 800,
            GranaryCapacity: 800);
    }
}
