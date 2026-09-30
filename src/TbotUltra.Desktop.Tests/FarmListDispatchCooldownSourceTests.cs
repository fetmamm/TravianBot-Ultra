using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListDispatchCooldownSourceTests
{
    [Fact]
    public void SuccessfulDispatchPaths_DoNotClearTheirOwnContinuousCooldown()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Farming.FarmLists.cs"));

        Assert.DoesNotContain(
            "WakeContinuousFarmScheduling();",
            MethodBody(source, "private async Task RefreshFarmListsUiAfterAutoSendIfNeededAsync", "private async Task<FarmListsViewResult?> TryApplyFarmListsSnapshotAsync"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WakeContinuousFarmScheduling();",
            MethodBody(source, "private async Task FarmListSendNowButtonClickAsync", "private async void FarmListSendAllNowButton_Click"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WakeContinuousFarmScheduling();",
            MethodBody(source, "private async Task FarmListSendAllNowButtonClickAsync", "private void SyncFarmingControlsEnabledState"),
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
