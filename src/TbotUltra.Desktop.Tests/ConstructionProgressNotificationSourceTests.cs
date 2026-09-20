using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionProgressNotificationSourceTests
{
    [Fact]
    public void ConfirmedTravianQueueProgress_ReachesDashboardBeforeTaskCompletion()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var resourceSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Resources",
            "TravianClient.Resources.Upgrade.cs"));
        var buildingSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Buildings",
            "TravianClient.Buildings.UpgradeFlow.cs"));
        var mainWindowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.xaml.cs"));

        Assert.Contains("PublishConstructionQueueObservationAsync", resourceSource, StringComparison.Ordinal);
        Assert.Contains("PublishConstructionQueueObservationAsync", buildingSource, StringComparison.Ordinal);
        Assert.Contains("_botService.ConstructionQueueObserved += OnConstructionQueueObserved", mainWindowSource, StringComparison.Ordinal);
    }

}
