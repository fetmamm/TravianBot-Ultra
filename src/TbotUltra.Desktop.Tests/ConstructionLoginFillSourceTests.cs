using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionLoginFillSourceTests
{
    [Fact]
    public void LoginFlow_PreparesOnlyTheLiveVerifiedBrowserVillage()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Session.cs"));

        Assert.DoesNotContain("PrepareConstructionLoginFill();", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            source.Split("PrepareConstructionLoginFillForActiveVerifiedVillage();", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ActiveVillageLoginFill_RequiresLiveAvailableSlotAndResetsOldScope()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.DeferredRefresh.cs"));

        Assert.Contains("private void PrepareConstructionLoginFillForActiveVerifiedVillage()", source, StringComparison.Ordinal);
        Assert.Contains("ClearConstructionLoginFillScope(\"login\")", source, StringComparison.Ordinal);
        Assert.Contains("ConstructionQueueAvailability.Available", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareLoginFill_PreservesPersistedQueueHumanizeExtra()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.DeferredRefresh.cs"));
        var methodStart = source.IndexOf(
            "private void PrepareConstructionLoginFill(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    private void ClearConstructionLoginFillForFullSlots(",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];

        Assert.DoesNotContain(
            "BotOptionPayloadKeys.QueueHumanizeExtraSeconds",
            methodBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedEmptyQueue_PreparesImmediateFillBeforeDeferredRefresh()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.VillageWorking.cs"));
        var methodStart = source.IndexOf(
            "private void CacheVillageStatus(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    // Re-keys any cached village status",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        var prepareIndex = methodBody.IndexOf(
            "PrepareConstructionLoginFill(",
            StringComparison.Ordinal);
        var refreshIndex = methodBody.IndexOf(
            "TriggerDeferredConstructionWaitRefresh(",
            StringComparison.Ordinal);

        Assert.True(prepareIndex >= 0);
        Assert.True(refreshIndex > prepareIndex);
    }

    [Fact]
    public void QueueFullCard_ShowsWaitingInsteadOfRetry()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.AutomationLoop.Ui.cs"));

        Assert.Contains(
            "ConstructionDeferReason.QueueFull => \"Waiting\"",
            source,
            StringComparison.Ordinal);
    }
}
