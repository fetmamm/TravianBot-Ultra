using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TroopTrainingResourceThresholdCalculatorTests
{
    [Fact]
    public void ResolveAutomaticResourceSelection_SelectsSlaveMilitiaClayCost()
    {
        var result = TroopTrainingResourceThresholdCalculator.ResolveAutomaticResourceSelection(
            new TroopTrainingCost(Wood: 45, Clay: 60, Iron: 30, Crop: 15),
            Resources(wood: 900, clay: 100, iron: 900, crop: 900),
            warehouseCapacity: 1000,
            granaryCapacity: 1000);

        Assert.Equal("clay", result.ResourceKey);
        Assert.False(result.CheckWood);
        Assert.True(result.CheckClay);
        Assert.False(result.CheckIron);
        Assert.False(result.CheckCrop);
    }

    [Fact]
    public void ResolveAutomaticResourceSelection_UsesLowestFillForEqualCosts()
    {
        var result = TroopTrainingResourceThresholdCalculator.ResolveAutomaticResourceSelection(
            new TroopTrainingCost(Wood: 60, Clay: 60, Iron: 30, Crop: 15),
            Resources(wood: 800, clay: 200, iron: 900, crop: 900),
            warehouseCapacity: 1000,
            granaryCapacity: 1000);

        Assert.Equal("clay", result.ResourceKey);
    }

    [Fact]
    public void ResolveAutomaticResourceSelection_ChangesWithSelectedTroop()
    {
        Assert.True(TroopCatalog.TryResolveTrainingCost("Egyptians", "Slave Militia", out var slaveMilitia));
        Assert.True(TroopCatalog.TryResolveTrainingCost("Egyptians", "Ash Warden", out var ashWarden));
        var resources = Resources(wood: 900, clay: 900, iron: 900, crop: 900);

        var first = TroopTrainingResourceThresholdCalculator.ResolveAutomaticResourceSelection(
            slaveMilitia, resources, 1000, 1000);
        var changed = TroopTrainingResourceThresholdCalculator.ResolveAutomaticResourceSelection(
            ashWarden, resources, 1000, 1000);

        Assert.Equal("clay", first.ResourceKey);
        Assert.Equal("iron", changed.ResourceKey);
    }

    [Fact]
    public void AutomaticSlaveMilitiaSelection_LowClayProducesSleepSizedDeadline()
    {
        Assert.True(TroopCatalog.TryResolveTrainingCost("Egyptians", "Slave Militia", out var cost));
        var resources = Resources(wood: 900, clay: 40, iron: 900, crop: 900);
        var selection = TroopTrainingResourceThresholdCalculator.ResolveAutomaticResourceSelection(
            cost,
            resources,
            warehouseCapacity: 1000,
            granaryCapacity: 1000);

        var result = TroopTrainingResourceThresholdCalculator.Evaluate(
            resources,
            new Dictionary<string, double?> { ["clay"] = 100 },
            warehouseCapacity: 1000,
            granaryCapacity: 1000,
            thresholdPercent: 90,
            selection.CheckWood,
            selection.CheckClay,
            selection.CheckIron,
            selection.CheckCrop,
            fallbackCooldownSeconds: 60);

        Assert.Equal("clay", selection.ResourceKey);
        Assert.False(result.IsReady);
        Assert.Equal(30_960, result.WaitSeconds);
        Assert.Equal("estimated_from_status", result.WaitReason);
    }

    [Fact]
    public void Evaluate_IsReadyWhenAnySelectedResourceMeetsThreshold()
    {
        var result = Evaluate(wood: 100, clay: 500, iron: 200, checkWood: true, checkClay: true, checkIron: true);

        Assert.True(result.IsReady);
        Assert.Equal("ready", result.WaitReason);
    }

    [Fact]
    public void Evaluate_TreatsExactThresholdAsReady()
    {
        var result = Evaluate(clay: 500, checkClay: true);

        Assert.True(result.IsReady);
    }

    [Fact]
    public void Evaluate_WaitsForEarliestSelectedResource()
    {
        var result = Evaluate(
            wood: 100,
            clay: 400,
            checkWood: true,
            checkClay: true,
            production: new Dictionary<string, double?> { ["wood"] = 200, ["clay"] = 100 });

        Assert.False(result.IsReady);
        Assert.Equal(3600, result.WaitSeconds);
        Assert.Equal("estimated_from_status", result.WaitReason);
    }

    [Fact]
    public void Evaluate_KnownPassingResourceWinsWhenAnotherCapacityIsUnknown()
    {
        var result = TroopTrainingResourceThresholdCalculator.Evaluate(
            Resources(clay: 500),
            new Dictionary<string, double?>(),
            warehouseCapacity: 1000,
            granaryCapacity: null,
            thresholdPercent: 50,
            checkWood: false,
            checkClay: true,
            checkIron: false,
            checkCrop: true,
            fallbackCooldownSeconds: 30);

        Assert.True(result.IsReady);
    }

    [Fact]
    public void Evaluate_RejectsEmptyResourceSelection()
    {
        var result = Evaluate();

        Assert.False(result.IsReady);
        Assert.Equal(30, result.WaitSeconds);
        Assert.Equal("no_resources_selected", result.WaitReason);
    }

    private static TroopTrainingResourceThresholdEvaluation Evaluate(
        long wood = 0,
        long clay = 0,
        long iron = 0,
        long crop = 0,
        bool checkWood = false,
        bool checkClay = false,
        bool checkIron = false,
        bool checkCrop = false,
        IReadOnlyDictionary<string, double?>? production = null)
        => TroopTrainingResourceThresholdCalculator.Evaluate(
            Resources(wood, clay, iron, crop),
            production ?? new Dictionary<string, double?>(),
            warehouseCapacity: 1000,
            granaryCapacity: 1000,
            thresholdPercent: 50,
            checkWood,
            checkClay,
            checkIron,
            checkCrop,
            fallbackCooldownSeconds: 30);

    private static Dictionary<string, long> Resources(long wood = 0, long clay = 0, long iron = 0, long crop = 0)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = wood,
            ["clay"] = clay,
            ["iron"] = iron,
            ["crop"] = crop,
        };
}
