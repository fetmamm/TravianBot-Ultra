using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ResourceSnapshotCalculatorTests
{
    [Fact]
    public void MergeProductionByHour_LiveValuesWinAndCacheFillsMissingValues()
    {
        var live = new Dictionary<string, double?> { ["wood"] = 120, ["clay"] = null };
        var cached = new Dictionary<string, double?> { ["wood"] = 90, ["clay"] = 80, ["iron"] = 70 };

        var result = ResourceSnapshotCalculator.MergeProductionByHour(live, cached);

        Assert.Equal(120, result["wood"]);
        Assert.Equal(80, result["clay"]);
        Assert.Equal(70, result["iron"]);
        Assert.Null(result["crop"]);
    }

    [Fact]
    public void OrderUpgradeCandidates_LowestLevelFirstWithoutStockSnapshot()
    {
        var fields = new[]
        {
            Field(3, "wood", 4),
            Field(2, "clay", 2),
            Field(1, "iron", 2),
            Field(null, "crop", 1),
        };

        var result = ResourceSnapshotCalculator.OrderUpgradeCandidates(fields, stockByType: null);

        Assert.Equal(new int?[] { 1, 2, 3 }, result.Select(field => field.SlotId));
    }

    [Fact]
    public void OrderUpgradeCandidates_SmartModeUsesStockThenLevelThenSlot()
    {
        var fields = new[]
        {
            Field(4, "wood", 1),
            Field(3, "clay", 5),
            Field(2, "iron", 3),
            Field(1, "iron", 3),
        };
        var stocks = new Dictionary<string, long> { ["wood"] = 500, ["clay"] = 100, ["iron"] = 100 };

        var result = ResourceSnapshotCalculator.OrderUpgradeCandidates(fields, stocks);

        Assert.Equal(new int?[] { 1, 2, 3, 4 }, result.Select(field => field.SlotId));
    }

    [Fact]
    public void BuildStorageForecasts_UsesGranaryForCropAndWarehouseForOtherResources()
    {
        var resources = new Dictionary<string, string>
        {
            ["wood"] = "500",
            ["clay"] = "0",
            ["iron"] = "1000",
            ["crop"] = "250",
        };
        var production = new Dictionary<string, double?>
        {
            ["wood"] = 100,
            ["clay"] = 0,
            ["iron"] = 100,
            ["crop"] = 250,
        };

        var result = ResourceSnapshotCalculator.BuildStorageForecasts(resources, 1000, 500, production);

        var wood = Assert.Single(result, item => item.ResourceKey == "wood");
        var crop = Assert.Single(result, item => item.ResourceKey == "crop");
        Assert.Equal(50, wood.PercentOfCapacity);
        Assert.Equal(18_000, wood.SecondsToFull);
        Assert.Equal(500, crop.Capacity);
        Assert.Equal(50, crop.PercentOfCapacity);
        Assert.Equal(3_600, crop.SecondsToFull);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(10, 11)]
    [InlineData(50_000, 43_200)]
    public void ComputeUpgradeWaitSeconds_AddsBufferAndCapsWait(int? detected, int expected)
    {
        Assert.Equal(expected, ResourceSnapshotCalculator.ComputeUpgradeWaitSeconds(detected));
    }

    [Fact]
    public void EvaluateUpgradeAffordability_UsesLongestResourceWait()
    {
        var resources = new Dictionary<string, string>
        {
            ["wood"] = "100",
            ["clay"] = "100",
            ["iron"] = "100",
            ["crop"] = "100",
        };
        var production = new Dictionary<string, double?>
        {
            ["wood"] = 100,
            ["clay"] = 200,
            ["iron"] = 100,
            ["crop"] = 100,
        };

        var result = ResourceSnapshotCalculator.EvaluateUpgradeAffordability(
            wood: 200, clay: 300, iron: 100, crop: 100, resources, production);

        Assert.False(result.HasUnknownWait);
        Assert.Equal(3_600, result.TimeUntilAffordableSeconds);
        Assert.Equal(700, result.TotalCost);
    }

    [Fact]
    public void EvaluateUpgradeAffordability_MarksMissingProductionAsUnknown()
    {
        var resources = new Dictionary<string, string> { ["wood"] = "0" };
        var production = new Dictionary<string, double?> { ["wood"] = null };

        var result = ResourceSnapshotCalculator.EvaluateUpgradeAffordability(
            wood: 1, clay: 0, iron: 0, crop: 0, resources, production);

        Assert.True(result.HasUnknownWait);
        Assert.Equal(long.MaxValue, result.TimeUntilAffordableSeconds);
    }

    private static ResourceField Field(int? slotId, string type, int? level)
        => new(slotId, type, type, level, null);
}
