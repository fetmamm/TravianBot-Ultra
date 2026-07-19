using System.Collections.Generic;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueueItemEstimateCalculatorTests
{
    private const int WarehouseGid = 10;

    [Fact]
    public void SumLevels_NullGidReturnsAlarmReason()
    {
        var (estimate, alarmReason) = QueueItemEstimateCalculator.SumLevels(null, 1, 3, 1.0, 1);

        Assert.False(estimate.HasData);
        Assert.Equal("building could not be matched to the catalog", alarmReason);
    }

    [Fact]
    public void SumLevels_TargetBelowFromLevelIsZeroWorkWithData()
    {
        var (estimate, alarmReason) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 5, 4, 1.0, 1);

        Assert.Null(alarmReason);
        Assert.True(estimate.HasData);
        Assert.Equal(0, estimate.Seconds);
        Assert.Equal(0, estimate.Wood);
    }

    [Fact]
    public void SumLevels_RangeSumEqualsSumOfSingleLevels()
    {
        var (levelOne, _) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 1, 1, 1.0, 1);
        var (levelTwo, _) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 2, 2, 1.0, 1);
        var (range, alarmReason) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 1, 2, 1.0, 1);

        Assert.Null(alarmReason);
        Assert.True(range.HasData);
        Assert.Equal(levelOne.Wood + levelTwo.Wood, range.Wood);
        Assert.Equal(levelOne.Crop + levelTwo.Crop, range.Crop);
        Assert.Equal(levelOne.Seconds + levelTwo.Seconds, range.Seconds, precision: 3);
    }

    [Fact]
    public void SumLevels_MissingCatalogLevelReturnsAlarmReason()
    {
        var (estimate, alarmReason) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 1, 99, 1.0, 1);

        Assert.False(estimate.HasData);
        Assert.NotNull(alarmReason);
        Assert.Contains("missing catalog data", alarmReason);
    }

    [Fact]
    public void SumLevelsWithQueueCoverage_CoverageFloorSkipsAlreadyQueuedLevels()
    {
        var coverage = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["v|slot:19"] = 4,
        };

        var (withCoverage, _) = QueueItemEstimateCalculator.SumLevelsWithQueueCoverage(
            WarehouseGid, currentLevel: 2, target: 5, 1.0, 1, "v|slot:19", coverage);
        var (stepOnly, _) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 5, 5, 1.0, 1);

        Assert.Equal(stepOnly.Wood, withCoverage.Wood);
        Assert.Equal(stepOnly.Seconds, withCoverage.Seconds, precision: 3);
        Assert.Equal(5, coverage["v|slot:19"]);
    }

    [Fact]
    public void SumLevelsWithQueueCoverage_UnknownFloorEstimatesTargetLevelOnly()
    {
        var coverage = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        var (estimate, _) = QueueItemEstimateCalculator.SumLevelsWithQueueCoverage(
            WarehouseGid, currentLevel: null, target: 7, 1.0, 1, "v|slot:19", coverage);
        var (targetOnly, _) = QueueItemEstimateCalculator.SumLevels(WarehouseGid, 7, 7, 1.0, 1);

        Assert.Equal(targetOnly.Wood, estimate.Wood);
        Assert.Equal(7, coverage["v|slot:19"]);
    }

    [Fact]
    public void SumLevelsWithQueueCoverage_NullKeySkipsCoverageBookkeeping()
    {
        var coverage = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        var (estimate, _) = QueueItemEstimateCalculator.SumLevelsWithQueueCoverage(
            WarehouseGid, currentLevel: 3, target: 4, 1.0, 1, coverageKey: null, coverage);

        Assert.True(estimate.HasData);
        Assert.Empty(coverage);
    }

    [Fact]
    public void SumLevelsWithQueueCoverage_CoverageNeverShrinks()
    {
        var coverage = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["v|slot:19"] = 8,
        };

        var (estimate, _) = QueueItemEstimateCalculator.SumLevelsWithQueueCoverage(
            WarehouseGid, currentLevel: 2, target: 5, 1.0, 1, "v|slot:19", coverage);

        Assert.True(estimate.HasData);
        Assert.Equal(0, estimate.Seconds);
        Assert.Equal(8, coverage["v|slot:19"]);
    }
}
