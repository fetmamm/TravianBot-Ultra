using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageStatusSweepSourceTests
{
    [Fact]
    public void Automation_VerifiesVillageMembershipBeforeSweepOrQueueMutation()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.ContinuousLoop.cs"));
        var methodStart = source.IndexOf(
            "private async ValueTask<AutomationStateSnapshot> ReadContinuousAutomationStateAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async ValueTask<AutomationActionOutcome> ExecuteContinuousAutomationActionAsync(",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        var preflight = methodBody.IndexOf(
            "EnsureVillageMembershipVerifiedBeforeAutomationAsync(options, cancellationToken)",
            StringComparison.Ordinal);
        var sweep = methodBody.IndexOf(
            "MaybeRunVillageStatusSweepAsync(options, cancellationToken",
            StringComparison.Ordinal);
        var queueSelection = methodBody.IndexOf(
            "SelectNextQueueItemForContinuousLoop()",
            StringComparison.Ordinal);

        Assert.True(preflight >= 0);
        Assert.True(sweep > preflight);
        Assert.True(queueSelection > preflight);
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
