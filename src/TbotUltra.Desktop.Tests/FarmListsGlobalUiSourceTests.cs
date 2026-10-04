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

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
