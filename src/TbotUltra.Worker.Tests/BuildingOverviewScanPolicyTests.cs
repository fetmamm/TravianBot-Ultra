using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BuildingOverviewScanPolicyTests
{
    [Fact]
    public void Evaluate_FullHydratedOverview_IsHighConfidenceAndDoesNotRetry()
    {
        var scan = BuildingOverviewScanPolicy.Evaluate(22, 0, 0, hasMainBuilding: true, hasRallyPoint: true);

        Assert.Equal(BuildingOverviewScanConfidence.High, scan.Confidence);
        Assert.False(BuildingOverviewScanPolicy.ShouldRetry(scan));
    }

    [Fact]
    public void Evaluate_PartialButUsefulOverview_DoesNotRetry()
    {
        var scan = BuildingOverviewScanPolicy.Evaluate(18, 5, 5, hasMainBuilding: true, hasRallyPoint: false);

        Assert.Equal(BuildingOverviewScanConfidence.Low, scan.Confidence);
        Assert.False(BuildingOverviewScanPolicy.ShouldRetry(scan));
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(22, false)]
    public void ShouldRetry_RequiresEnoughSlotsAndMainBuilding(int slotCount, bool hasMainBuilding)
    {
        var scan = BuildingOverviewScanPolicy.Evaluate(slotCount, 0, 0, hasMainBuilding, hasRallyPoint: true);

        Assert.True(BuildingOverviewScanPolicy.ShouldRetry(scan));
    }

    [Fact]
    public void PreferSecond_UsesSameQualityWeightsAsRuntimeScanSelection()
    {
        var first = BuildingOverviewScanPolicy.Evaluate(18, 4, 3, hasMainBuilding: true, hasRallyPoint: false);
        var second = BuildingOverviewScanPolicy.Evaluate(22, 0, 0, hasMainBuilding: true, hasRallyPoint: true);

        Assert.True(BuildingOverviewScanPolicy.PreferSecond(first, second));
        Assert.False(BuildingOverviewScanPolicy.PreferSecond(second, first));
    }

    [Fact]
    public void Describe_PreservesDiagnosticLogShape()
    {
        var scan = BuildingOverviewScanPolicy.Evaluate(17, 2, 1, hasMainBuilding: false, hasRallyPoint: true);

        Assert.Equal("slots=17, missing_gid=2, unknown_level=1, main=missing, rally=ok", BuildingOverviewScanPolicy.Describe(scan));
    }
}
