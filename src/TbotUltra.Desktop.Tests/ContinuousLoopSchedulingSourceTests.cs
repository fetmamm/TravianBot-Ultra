using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousLoopSchedulingSourceTests
{
    [Fact]
    public void FarmListToggle_UpdatesPayloadWithoutChangingCooldownDeadline()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Farming.FarmLists.cs"));
        var methodStart = source.IndexOf(
            "private void RefreshQueuedContinuousFarmListSelections()",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    private void ApplyFarmingSettingsToUi(BotOptions options)",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        Assert.Contains("UpdateDeferredQueueItem(item.Id, updatedPayload)", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PatchDeferredQueueItem", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("NextAttemptAt", methodBody, StringComparison.Ordinal);
    }
}
