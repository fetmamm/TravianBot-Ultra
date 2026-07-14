using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class UpgradeResourceWaitCalculatorTests
{
    [Fact]
    public void BuildSnapshot_UsesCachedProductionForLongestShortfall()
    {
        var snapshot = UpgradeResourceWaitCalculator.BuildSnapshot(
            "Building slot 19 (Warehouse) upgrade to level 7",
            ResourceLongs(wood: 200, clay: 100, iron: 100, crop: 100),
            ResourceStrings(wood: 100, clay: 100, iron: 100, crop: 100),
            ResourceProduction(wood: 50, clay: 50, iron: 50, crop: 50),
            fallbackWaitSeconds: 0,
            waitReasonWhenEstimated: "cached_production",
            warehouseCapacity: 1000,
            granaryCapacity: 1000);

        Assert.Equal(7200, snapshot.WaitSeconds);
        Assert.Equal("cached_production", snapshot.WaitReason);
        Assert.Equal("from_cached_production", snapshot.Values["wood"].WaitReason);
        Assert.Equal(100, snapshot.Values["wood"].Missing);
    }

    [Fact]
    public void BuildSnapshot_ClassifiesStorageCapacityBlock()
    {
        var snapshot = UpgradeResourceWaitCalculator.BuildSnapshot(
            "Building slot 19 (Warehouse) upgrade to level 7",
            ResourceLongs(wood: 1100, clay: 100, iron: 100, crop: 100),
            ResourceStrings(wood: 100, clay: 100, iron: 100, crop: 100),
            ResourceProduction(wood: 50, clay: 50, iron: 50, crop: 50),
            fallbackWaitSeconds: 0,
            waitReasonWhenEstimated: "cached_production",
            warehouseCapacity: 1000,
            granaryCapacity: 1000);

        Assert.Equal("storage_capacity", snapshot.WaitReason);
        Assert.Equal("warehouse", snapshot.StorageCapacityKind);
        Assert.Contains("queue_wait_seconds=", UpgradeResourceWaitCalculator.BuildBlockedResultMessage(snapshot));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(5, 5300)]
    public void ComputePostActionWaitMs_PreservesExistingTiming(int seconds, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, UpgradeResourceWaitCalculator.ComputePostActionWaitMs(seconds));
    }

    private static IReadOnlyDictionary<string, long?> ResourceLongs(long wood, long clay, long iron, long crop) =>
        new Dictionary<string, long?> { ["wood"] = wood, ["clay"] = clay, ["iron"] = iron, ["crop"] = crop };

    private static IReadOnlyDictionary<string, string> ResourceStrings(long wood, long clay, long iron, long crop) =>
        new Dictionary<string, string> { ["wood"] = wood.ToString(), ["clay"] = clay.ToString(), ["iron"] = iron.ToString(), ["crop"] = crop.ToString() };

    private static IReadOnlyDictionary<string, double?> ResourceProduction(double wood, double clay, double iron, double crop) =>
        new Dictionary<string, double?> { ["wood"] = wood, ["clay"] = clay, ["iron"] = iron, ["crop"] = crop };
}
