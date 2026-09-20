using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class PostLoginRefreshDecisionsTests
{
    [Fact]
    public void ShouldReadCurrentVillageStatus_CompleteFirstLoginSnapshotWithoutVillageNavigation_ReturnsFalse()
    {
        var shouldRead = PostLoginRefreshDecisions.ShouldReadCurrentVillageStatus(
            officialServer: true,
            newAccountAnalysisPending: true,
            newVillagesAnalyzed: true,
            newVillageAnalysisNavigated: false);

        Assert.False(shouldRead);
    }

    [Fact]
    public void ShouldReadCurrentVillageStatus_FirstLoginThatNavigatedBetweenVillages_ReturnsTrue()
    {
        var shouldRead = PostLoginRefreshDecisions.ShouldReadCurrentVillageStatus(
            officialServer: true,
            newAccountAnalysisPending: true,
            newVillagesAnalyzed: true,
            newVillageAnalysisNavigated: true);

        Assert.True(shouldRead);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldReadCurrentVillageStatus_ExistingRefreshCasesRemainEnabled(
        bool officialServer,
        bool newAccountAnalysisPending)
    {
        var shouldRead = PostLoginRefreshDecisions.ShouldReadCurrentVillageStatus(
            officialServer,
            newAccountAnalysisPending,
            newVillagesAnalyzed: true,
            newVillageAnalysisNavigated: false);

        Assert.True(shouldRead);
    }

    [Fact]
    public void ShouldReadCurrentVillageStatus_IncompleteFirstLoginAnalysis_ReturnsTrue()
    {
        var shouldRead = PostLoginRefreshDecisions.ShouldReadCurrentVillageStatus(
            officialServer: true,
            newAccountAnalysisPending: true,
            newVillagesAnalyzed: false,
            newVillageAnalysisNavigated: false);

        Assert.True(shouldRead);
    }
}
