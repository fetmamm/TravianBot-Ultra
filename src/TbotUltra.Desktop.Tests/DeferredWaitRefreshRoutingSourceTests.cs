using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class DeferredWaitRefreshRoutingSourceTests
{
    [Fact]
    public void ResourceAndStorageUiRefresh_DoNotPublishTheSameStatusTwice()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TbotUltra.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(
            root.FullName, "src", "TbotUltra.Desktop", "MainWindow.Resources.Snapshot.cs"));
        var resourceStart = source.IndexOf("private void ApplyResourceStatusToUi", StringComparison.Ordinal);
        var storageStart = source.IndexOf("private void ApplyStorageStatusToUi", resourceStart, StringComparison.Ordinal);
        var storageEnd = source.IndexOf("private void TriggerProductionBackfillIfUnknown", storageStart, StringComparison.Ordinal);
        Assert.True(resourceStart >= 0 && storageStart > resourceStart && storageEnd > storageStart);

        Assert.Contains("CacheVillageStatus(status, triggerDeferredWaitRefresh: false)", source[resourceStart..storageStart], StringComparison.Ordinal);
        Assert.Contains("CacheVillageStatus(status, triggerDeferredWaitRefresh: false)", source[storageStart..storageEnd], StringComparison.Ordinal);
    }
}
