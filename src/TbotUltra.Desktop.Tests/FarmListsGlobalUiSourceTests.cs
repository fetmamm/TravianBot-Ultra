using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListsGlobalUiSourceTests
{
    [Fact]
    public void VillageSelection_DoesNotReplaceAccountWideFarmListRowsOrTimers()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var villageSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.VillageWorking.cs"));
        var farmingSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Farming.FarmLists.cs"));

        var selectionBody = MethodBody(
            villageSource,
            "private void ShowSelectedVillageFromCache",
            "private void ApplySelectedVillageTribeFromCache");

        Assert.DoesNotContain("cached.FarmLists", selectionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyCachedFarmListsToUiAsync", selectionBody, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FarmLists = result.Lists",
            farmingSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NextSendBadge_UsesTheAccountWideFarmQueueInsteadOfTheSelectedVillageCard()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Farming.FarmLists.cs"));

        var body = MethodBody(
            source,
            "private void UpdateNextFarmListSendDisplay",
            "private void WakeContinuousFarmScheduling");

        Assert.Contains("GetQueueSnapshotForUi()", body, StringComparison.Ordinal);
        Assert.Contains("send_farmlists", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_automationLoopTasks", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IsQueueItemForSelectedVillageOrGlobal", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
