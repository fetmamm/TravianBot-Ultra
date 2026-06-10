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
    public void IsVerbose_HidesRequestedDiagnosticLinesInCleanMode(string message)
    {
        Assert.True(LogClassifier.IsVerbose(message));
    }

    [Theory]
    [InlineData("Removed 12/12 invalid coordinate(s) from Travco list 'Travco all pages_1'.")]
    [InlineData("[travco] removed 12 invalid coordinate(s) from 'Travco all pages_1'.")]
    [InlineData("Finished 'Inactive1': added=17, duplicates=0, invalid=12, failed=12.")]
    public void IsExpectedFarmListResult_MatchesNormalOutcomeLines(string message)
    {
        Assert.True(LogClassifier.IsExpectedFarmListResult(message));
    }
}
