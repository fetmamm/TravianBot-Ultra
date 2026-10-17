using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueueRefreshCoalescingSourceTests
{
    [Fact]
    public void DeferredBackgroundRefreshes_UseCoalescedQueueProjection()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Desktop",
            "MainWindow.DeferredRefresh.cs"));

        Assert.DoesNotContain("RefreshQueueUi();", source, StringComparison.Ordinal);
        Assert.Contains("RequestQueueUiRefresh();", source, StringComparison.Ordinal);
    }
}
