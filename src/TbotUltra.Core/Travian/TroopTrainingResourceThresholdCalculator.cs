namespace TbotUltra.Core.Travian;

public sealed record TroopTrainingResourceThresholdEvaluation(
    bool IsReady,
    int WaitSeconds,
    string WaitReason,
    IReadOnlyDictionary<string, long> RequiredResources);

public sealed record TroopTrainingResourceSelection(
    string ResourceKey,
    bool CheckWood,
    bool CheckClay,
    bool CheckIron,
    bool CheckCrop);

/// <summary>
/// Evaluates the Build troops percentage trigger. Selected resources use OR semantics: training is ready
/// when any selected resource reaches the threshold, and otherwise waits for the earliest selected resource.
/// </summary>
public static class TroopTrainingResourceThresholdCalculator
{
    public static TroopTrainingResourceSelection ResolveAutomaticResourceSelection(
        TroopTrainingCost cost,
        IReadOnlyDictionary<string, long> currentResources,
        long? warehouseCapacity,
        long? granaryCapacity)
    {
        var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = cost.Wood,
            ["clay"] = cost.Clay,
            ["iron"] = cost.Iron,
            ["crop"] = cost.Crop,
        };
        var highestCost = costs.Values.Max();
        var resourceKey = costs
            .Where(pair => pair.Value == highestCost)
            .OrderBy(pair => ResourceFillRatio(pair.Key, currentResources, warehouseCapacity, granaryCapacity))
            .ThenBy(pair => Array.IndexOf(["wood", "clay", "iron", "crop"], pair.Key))
            .Select(pair => pair.Key)
            .First();

        return new TroopTrainingResourceSelection(
            resourceKey,
            CheckWood: resourceKey == "wood",
            CheckClay: resourceKey == "clay",
            CheckIron: resourceKey == "iron",
            CheckCrop: resourceKey == "crop");
    }

    public static TroopTrainingResourceThresholdEvaluation Evaluate(
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        long? warehouseCapacity,
        long? granaryCapacity,
        int thresholdPercent,
        bool checkWood,
        bool checkClay,
        bool checkIron,
        bool checkCrop,
        int fallbackCooldownSeconds)
    {
        var selectedKeys = new List<string>(4);
        if (checkWood) selectedKeys.Add("wood");
        if (checkClay) selectedKeys.Add("clay");
        if (checkIron) selectedKeys.Add("iron");
        if (checkCrop) selectedKeys.Add("crop");

        var required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 0,
            ["clay"] = 0,
            ["iron"] = 0,
            ["crop"] = 0,
        };
        var fallback = Math.Max(1, fallbackCooldownSeconds);
        if (selectedKeys.Count == 0)
        {
            return new TroopTrainingResourceThresholdEvaluation(false, fallback, "no_resources_selected", required);
        }

        var threshold = Math.Clamp(thresholdPercent, 0, 100);
        if (threshold == 0)
        {
            return new TroopTrainingResourceThresholdEvaluation(true, 0, "ready", required);
        }

        var waits = new List<(int Seconds, string Reason)>(selectedKeys.Count);
        foreach (var key in selectedKeys)
        {
            var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? granaryCapacity
                : warehouseCapacity;
            if (capacity is not > 0)
            {
                waits.Add((fallback, "recheck_needed"));
                continue;
            }

            var resourceRequirement = (long)Math.Ceiling(capacity.Value * (threshold / 100d));
            required[key] = resourceRequirement;
            currentResources.TryGetValue(key, out var currentValue);
            var missing = Math.Max(0, resourceRequirement - Math.Max(0, currentValue));
            if (missing == 0)
            {
                return new TroopTrainingResourceThresholdEvaluation(true, 0, "ready", required);
            }

            productionByHour.TryGetValue(key, out var production);
            waits.Add(production > 0
                ? (Math.Max(1, (int)Math.Ceiling((missing / production.Value) * 3600d)), "estimated_from_status")
                : (fallback, "recheck_needed"));
        }

        var earliest = waits
            .OrderBy(item => item.Seconds)
            .ThenBy(item => string.Equals(item.Reason, "recheck_needed", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();
        return new TroopTrainingResourceThresholdEvaluation(false, earliest.Seconds, earliest.Reason, required);
    }

    private static double ResourceFillRatio(
        string key,
        IReadOnlyDictionary<string, long> currentResources,
        long? warehouseCapacity,
        long? granaryCapacity)
    {
        currentResources.TryGetValue(key, out var current);
        var capacity = key == "crop" ? granaryCapacity : warehouseCapacity;
        return capacity is > 0 ? Math.Max(0, current) / (double)capacity.Value : Math.Max(0, current);
    }
}
