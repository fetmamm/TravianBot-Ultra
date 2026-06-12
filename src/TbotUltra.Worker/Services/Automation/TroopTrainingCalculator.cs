using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless troop-training resource math extracted from <see cref="TravianClient"/>:
/// trainable amount given a resource reserve, required current resources, missing-resource
/// wait estimation, queue-limit/remaining parsing, and capacity merging. Pure functions so
/// they can be unit-tested in isolation.
/// <para>
/// Note: <c>MergeTroopTrainingProductionByHour</c> stays on <see cref="TravianClient"/> because
/// it builds on the general-purpose resource-snapshot helper <c>MergeProductionByHour</c>, which
/// is shared with non-troop resource reading.
/// </para>
/// </summary>
internal static class TroopTrainingCalculator
{
    internal static int? TryParseTroopTrainingQueueLimitSeconds(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || string.Equals(mode, "no_limit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(mode.Trim(), out var hours) && hours > 0
            ? hours * 3600
            : null;
    }

    internal static int ResolveTroopTrainingQueueRemainingSeconds(IReadOnlyList<BuildQueueItem> items)
    {
        return items
            .Select(item => TravianParsing.ParseDurationToSeconds(item.TimeLeft))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Sum();
    }

    internal static int CalculateTroopTrainingAmount(
        IReadOnlyDictionary<string, long> resources,
        int woodCost,
        int clayCost,
        int ironCost,
        int cropCost,
        string amountMode,
        int keepResourcesPercent)
    {
        if (woodCost <= 0 || clayCost <= 0 || ironCost <= 0 || cropCost <= 0)
        {
            return 0;
        }

        var normalizedMode = NormalizeTroopTrainingAmountMode(amountMode);
        var reserveFactor = normalizedMode == "keep_resources"
            ? Math.Clamp(keepResourcesPercent, 0, 95) / 100d
            : 0d;

        var woodAvailable = ComputeAvailableResource(resources, "wood", reserveFactor);
        var clayAvailable = ComputeAvailableResource(resources, "clay", reserveFactor);
        var ironAvailable = ComputeAvailableResource(resources, "iron", reserveFactor);
        var cropAvailable = ComputeAvailableResource(resources, "crop", reserveFactor);

        var trainable = new[]
        {
            woodAvailable / woodCost,
            clayAvailable / clayCost,
            ironAvailable / ironCost,
            cropAvailable / cropCost,
        }.Min();

        return trainable >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Max(0L, trainable);
    }

    internal static string NormalizeTroopTrainingAmountMode(string? amountMode)
    {
        return string.Equals(amountMode, "keep_resources", StringComparison.OrdinalIgnoreCase)
            ? "keep_resources"
            : "maximum";
    }

    internal static IReadOnlyDictionary<string, long> CalculateTroopTrainingRequiredResources(
        int woodCost,
        int clayCost,
        int ironCost,
        int cropCost,
        string amountMode,
        int keepResourcesPercent,
        int troopCount)
    {
        var count = Math.Max(1, troopCount);
        return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = CalculateRequiredCurrentResource(woodCost, count, amountMode, keepResourcesPercent),
            ["clay"] = CalculateRequiredCurrentResource(clayCost, count, amountMode, keepResourcesPercent),
            ["iron"] = CalculateRequiredCurrentResource(ironCost, count, amountMode, keepResourcesPercent),
            ["crop"] = CalculateRequiredCurrentResource(cropCost, count, amountMode, keepResourcesPercent),
        };
    }

    internal static int EstimateTroopTrainingWaitSeconds(
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, long> requiredResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        int fallbackCooldownSeconds = 60)
    {
        var hasUnknownWait = false;
        var longestFiniteWait = 0;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            requiredResources.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentValue);
            var missing = Math.Max(0, requiredValue - currentValue);
            if (missing <= 0)
            {
                continue;
            }

            productionByHour.TryGetValue(key, out var production);
            if (production > 0)
            {
                var waitSeconds = (int)Math.Ceiling((missing / production.Value) * 3600d);
                longestFiniteWait = Math.Max(longestFiniteWait, Math.Max(1, waitSeconds));
                continue;
            }

            hasUnknownWait = true;
        }

        if (longestFiniteWait <= 0)
        {
            return fallbackCooldownSeconds;
        }

        return hasUnknownWait
            ? Math.Max(Math.Min(fallbackCooldownSeconds, 60), Math.Min(longestFiniteWait, 60))
            : longestFiniteWait;
    }

    internal static ResourceCapacitySnapshot MergeTroopTrainingCapacities(
        ResourceCapacitySnapshot liveCapacities,
        ResourceCapacitySnapshot statusCapacities,
        long? cachedWarehouseCapacity,
        long? cachedGranaryCapacity)
    {
        return new ResourceCapacitySnapshot(
            liveCapacities.WarehouseCapacity is > 0
                ? liveCapacities.WarehouseCapacity
                : statusCapacities.WarehouseCapacity is > 0
                    ? statusCapacities.WarehouseCapacity
                    : cachedWarehouseCapacity,
            liveCapacities.GranaryCapacity is > 0
                ? liveCapacities.GranaryCapacity
                : statusCapacities.GranaryCapacity is > 0
                    ? statusCapacities.GranaryCapacity
                    : cachedGranaryCapacity);
    }

    internal static long GetResource(IReadOnlyDictionary<string, long> resources, string key)
    {
        return resources.TryGetValue(key, out var value) ? Math.Max(0L, value) : 0L;
    }

    private static long ComputeAvailableResource(IReadOnlyDictionary<string, long> resources, string key, double reserveFactor)
    {
        var current = GetResource(resources, key);
        if (current <= 0)
        {
            return 0;
        }

        var reserve = reserveFactor <= 0
            ? 0L
            : (long)Math.Floor(current * reserveFactor);
        return Math.Max(0L, current - reserve);
    }

    private static long CalculateRequiredCurrentResource(int unitCost, int troopCount, string amountMode, int keepResourcesPercent)
    {
        if (unitCost <= 0 || troopCount <= 0)
        {
            return 0;
        }

        var totalCost = (long)unitCost * troopCount;
        if (!string.Equals(NormalizeTroopTrainingAmountMode(amountMode), "keep_resources", StringComparison.OrdinalIgnoreCase))
        {
            return totalCost;
        }

        var reserveFactor = Math.Clamp(keepResourcesPercent, 0, 95) / 100d;
        if (reserveFactor <= 0)
        {
            return totalCost;
        }

        var denominator = Math.Max(0.05d, 1d - reserveFactor);
        long low = totalCost;
        long high = Math.Max(low, (long)Math.Ceiling(totalCost / denominator) + 2);
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            var available = ComputeAvailableResource(
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["value"] = mid },
                "value",
                reserveFactor);
            if (available >= totalCost)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}
