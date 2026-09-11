using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingTemplateQueuePreflightSourceTests
{
    [Fact]
    public void TemplateQueue_CombinesResourceAndConstructionStorageIntoOneConfirmation()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.Buildings.cs"));
        var start = source.IndexOf("private void QueueBuildingTemplatePlans", StringComparison.Ordinal);
        var end = source.IndexOf("private void HandleBuildingSlotSelection", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Equal(1, Count(method, "AppDialog.ShowCustomContent("));
        Assert.Equal(1, Count(method, "_buildingsPanelService.EnqueueBatch(finalRequests)"));
        Assert.Contains("storageVillages", method, StringComparison.Ordinal);
        Assert.Contains("All selected villages are shown together", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateQueue_RepaintsSelectedBuildingsImmediatelyFromPersistedQueue()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.Buildings.cs"));
        var start = source.IndexOf("private void QueueBuildingTemplatePlans", StringComparison.Ordinal);
        var end = source.IndexOf("private bool TryPrepareBuildingTemplateVillage", start, StringComparison.Ordinal);
        var method = source[start..end];

        var enqueue = method.IndexOf("_buildingsPanelService.EnqueueBatch(finalRequests)", StringComparison.Ordinal);
        var immediateRefresh = method.IndexOf(
            "RequestQueueUiRefresh(selectId: created.LastOrDefault()?.Id, immediate: true)",
            StringComparison.Ordinal);
        var repaint = method.IndexOf(
            "PopulateBuildingsTab(selectedStatus, requestQueueEstimateRefresh: false)",
            StringComparison.Ordinal);
        var autoRun = method.IndexOf("TriggerQueueAutoRunFromEnqueue()", StringComparison.Ordinal);

        Assert.True(enqueue >= 0);
        Assert.True(immediateRefresh > enqueue);
        Assert.True(repaint > immediateRefresh);
        Assert.True(autoRun > repaint);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }
}
