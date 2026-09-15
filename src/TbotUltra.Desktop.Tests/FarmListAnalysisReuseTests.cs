using Xunit;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListAnalysisReuseTests
{
    [Fact]
    public void CanReuseRecentAnalysis_AcceptsFreshAnalysis()
    {
        var now = new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero);

        var workflow = CreateWorkflow();
        workflow.CaptureAutomationState([RealRow()], now.AddSeconds(-42));

        Assert.True(workflow.CanReuseRecentAnalysis(now));
    }

    [Fact]
    public void CanReuseRecentAnalysis_RejectsMissingOrStaleAnalysis()
    {
        var now = new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero);

        var workflow = CreateWorkflow();

        Assert.False(workflow.CanReuseRecentAnalysis(now));
        workflow.CaptureAutomationState([RealRow()], now.AddMinutes(-6));
        Assert.False(workflow.CanReuseRecentAnalysis(now));
    }

    [Fact]
    public void AutomationSnapshot_ProjectsSelectionWithoutWpfDispatcherRead()
    {
        var workflow = CreateWorkflow();
        workflow.CaptureAutomationState(
        [
            RealRow(),
            new FarmListStatusRow { Name = "Disabled", IsEnabled = false },
            new FarmListStatusRow { IsPlaceholder = true },
        ], DateTimeOffset.UtcNow);

        var snapshot = workflow.AutomationSnapshot;

        Assert.Equal(2, snapshot.TotalCount);
        Assert.Equal(["Raiders"], snapshot.SelectedNames);
        Assert.Equal(["Raiders", "Disabled"], snapshot.AvailableNames);
        Assert.False(snapshot.NeedsAnalysis);
    }

    private static FarmListsWorkflow CreateWorkflow() => new(null!, null!, null!, string.Empty, () => string.Empty, _ => { });

    private static FarmListStatusRow RealRow() => new() { Name = "Raiders", IsEnabled = true };
}
