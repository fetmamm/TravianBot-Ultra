using TbotUltra.Desktop.Services.Logging;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LogClassifierTests
{
    [Theory]
    [InlineData("[construction-queue:verbose] blocker active village='KLET' waitSeconds=120")]
    [InlineData("[construction-queue:verbose] candidate blocked candidateTask='construct_building'")]
    [InlineData("[construction-queue:verbose] queue-full retry released village='KLET'")]
    [InlineData("[construction-queue:verbose] village queue blocked village='KLET' blockedItems=18")]
    [InlineData("[construction-queue:verbose] queue-full retry selected village='KLET'")]
    public void IsVerbose_HidesConstructionQueueDiagnosticsInCleanMode(string message)
    {
        Assert.True(LogClassifier.IsVerbose(message));
    }

    [Fact]
    public void IsVerbose_DoesNotHideConstructionQueuePersistenceFailure()
    {
        Assert.False(LogClassifier.IsVerbose(
            "[construction-queue] construction payload persistence failed id=123 task='construct_building'"));
    }

    [Theory]
    [InlineData("Hero status: dead=False, hp=?%, adventures=0, points=0, in_village=True. No hero action was needed.")]
    [InlineData("[hero_manage STARTED] (1/1) on 'account'")]
    [InlineData("[LoginAsync started]")]
    [InlineData("[HandleResourceSnapshotRefreshTickAsync] 20s tick")]
    public void IsVerbose_HidesRequestedDiagnosticLinesInCleanMode(string message)
    {
        Assert.True(LogClassifier.IsVerbose(message));
    }

    [Theory]
    [InlineData("[pacing] Click: waiting 2.3s")]
    [InlineData("[pacing] Task: before task: waiting 4.0s")]
    [InlineData("[pacing] session run timer started; next sleep in 00:42:10.")]
    [InlineData("Session sleep (manual) sleep starting; sleeping for 00:30:00.")]
    [InlineData("Session waking - resuming.")]
    [InlineData("[LOOP 3] WAIT 30s")]
    [InlineData("[AUTOQ 5] WAIT 664s for deferred task=upgrade_all_resources_to_level")]
    [InlineData("[keep-alive:verbose] skipped because no continuous-loop work is due soon.")]
    public void Classify_RoutesSessionActionAndWaitLinesToPacing(string message)
    {
        Assert.Equal(LogCategory.Pacing, LogClassifier.Classify(message));
    }

    [Theory]
    [InlineData("[LOOP 3] PICK group=Construction, task=upgrade_building_to_level")]
    [InlineData("[loop-pick:verbose] group=Construction skipped (no ready construction items)")]
    public void Classify_KeepsNonWaitLoopLinesInLoop(string message)
    {
        Assert.Equal(LogCategory.Loop, LogClassifier.Classify(message));
    }

    [Theory]
    [InlineData("Removed 12/12 invalid coordinate(s) from Travco list 'Travco all pages_1'.")]
    [InlineData("[travco] removed 12 invalid coordinate(s) from 'Travco all pages_1'.")]
    [InlineData("Finished 'Inactive1': added=17, duplicates=0, invalid=12, failed=12.")]
    public void IsExpectedFarmListResult_MatchesNormalOutcomeLines(string message)
    {
        Assert.True(LogClassifier.IsExpectedFarmListResult(message));
    }

    [Theory]
    [InlineData("[queue] FAIL task='upgrade' TransientNavigationException: Navigation to 'dorf2.php' timed out after safe retries.")]
    [InlineData("[upgrade FAILED] TransientNavigationException: Navigation to 'dorf1.php' timed out after safe retries.")]
    [InlineData("[LOOP 5] TRANSIENT | TransientNavigationException: Navigation to 'dorf2.php' timed out after safe retries.")]
    public void IsSafeTransientRetry_MatchesDeferredNavigationFailures(string message)
    {
        Assert.True(LogClassifier.IsSafeTransientRetry(message));
    }

    [Theory]
    [InlineData("ALARM: controlled relogin failed")]
    [InlineData("TimeoutException: state-changing click timed out")]
    public void IsSafeTransientRetry_DoesNotHideActionableFailures(string message)
    {
        Assert.False(LogClassifier.IsSafeTransientRetry(message));
    }
}
