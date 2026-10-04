using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageStatusSweepSourceTests
{
    [Fact]
    public void CompletedVillageVisit_RepaintsSelectedVillageResourceAndBuildingDetails()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.ContinuousLoop.cs"));

        Assert.Contains("ApplyVillageStatusSweepToUi(status);", source, StringComparison.Ordinal);
        Assert.Contains(
            "ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("PopulateBuildingsTab(status", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanNowButton_IsBoundToTheVillageStatusRoundCommand()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "SettingsWindow.xaml"));

        Assert.Contains("Content=\"Scan now\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SettingsVm.RunVillageStatusSweepNowCommand}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DorfTooltips_DescribeTheirActualScanResponsibilities()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "SettingsWindow.xaml"));

        Assert.Contains(
            "Reads resources, hourly production, Warehouse and Granary capacity, population, construction queue, tasks, daily quests, unread messages and reports.",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Reads all buildings in the village center. Required before Smithy, Barracks, Stable, Workshop, Town Hall or Brewery can be scanned.",
            xaml,
            StringComparison.Ordinal);
    }
}
