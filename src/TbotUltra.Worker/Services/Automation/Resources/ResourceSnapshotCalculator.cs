using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless resource snapshot calculations. DOM reads and navigation remain in
/// <see cref="TravianClient"/>.
/// </summary>
internal static class ResourceSnapshotCalculator
{
    private static readonly string[] ResourceKeys = ["wood", "clay", "iron", "crop"];

    internal static IReadOnlyDictionary<string, double?> MergeProductionByHour(
        IReadOnlyDictionary<string, double?> live,
        IReadOnlyDictionary<string, double?>? cached)
    {
        var merged = ResourceKeys.ToDictionary(
            key => key,
            _ => (double?)null,
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in ResourceKeys)
        {
            live.TryGetValue(key, out var liveValue);
            if (liveValue is not null)
            {
                merged[key] = liveValue;
                continue;
            }

            if (cached is not null && cached.TryGetValue(key, out var cachedValue))
            {
                merged[key] = cachedValue;
            }
        }

        return merged;
    }

    internal static IReadOnlyList<ResourceField> OrderUpgradeCandidates(
        IEnumerable<ResourceField> fields,
        IReadOnlyDictionary<string, long>? stockByType)
    {
        var actionable = fields.Where(field => field.SlotId is not null && field.Level is not null);
        return stockByType is null
            ? actionable
                .OrderBy(field => field.Level ?? 0)
                .ThenBy(field => field.SlotId ?? 999)
                .ToList()
            : actionable
                .OrderBy(field => stockByType.TryGetValue(field.FieldType, out var stock) ? stock : long.MaxValue)
                .ThenBy(field => field.Level ?? 0)
                .ThenBy(field => field.SlotId ?? 999)
                .ToList();
    }

    internal static IReadOnlyList<ResourceStorageForecast> BuildStorageForecasts(
        IReadOnlyDictionary<string, string> resources,
        long? warehouseCapacity,
        long? granaryCapacity,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        var result = new List<ResourceStorageForecast>(ResourceKeys.Length);
        foreach (var key in ResourceKeys)
        {
            resources.TryGetValue(key, out var rawCurrent);
            var current = TravianParsing.TryParseResourceValue(rawCurrent);
            var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? granaryCapacity
                : warehouseCapacity;

            productionByHour.TryGetValue(key, out var production);
            double? percent = null;
            if (capacity is > 0 && current is not null)
            {
                percent = Math.Clamp((double)current.Value / capacity.Value * 100.0, 0.0, 100.0);
            }

            int? secondsToFull = null;
            if (capacity is > 0 && current is not null && production is > 0)
            {
                var remaining = Math.Max(0L, capacity.Value - current.Value);
                var computedSeconds = Math.Ceiling((remaining / production.Value) * 3600.0);
                secondsToFull = computedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)computedSeconds;
            }

            result.Add(new ResourceStorageForecast(
                ResourceKey: key,
                Current: current,
                Capacity: capacity,
                PercentOfCapacity: percent,
                ProductionPerHour: production,
                SecondsToFull: secondsToFull));
        }

        return result;
    }

    internal static int ComputeUpgradeWaitSeconds(int? detectedSeconds)
    {
        var seconds = Math.Max(0, detectedSeconds ?? 0);
        return seconds == 0 ? 0 : Math.Min(seconds + 1, 12 * 60 * 60);
    }

    internal static ResourceUpgradeAffordability EvaluateUpgradeAffordability(
        long wood,
        long clay,
        long iron,
        long crop,
        IReadOnlyDictionary<string, string> resources,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        var requiredByResource = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = wood,
            ["clay"] = clay,
            ["iron"] = iron,
            ["crop"] = crop,
        };

        long longest = 0;
        var hasUnknownWait = false;
        foreach (var (key, required) in requiredByResource)
        {
            resources.TryGetValue(key, out var currentRaw);
            var current = TravianParsing.TryParseResourceValue(currentRaw) ?? 0;
            var missing = Math.Max(0, required - current);
            if (missing <= 0)
            {
                continue;
            }

            productionByHour.TryGetValue(key, out var production);
            if (production is > 0)
            {
                var wait = (long)Math.Ceiling((missing / production.Value) * 3600d);
                longest = Math.Max(longest, Math.Max(1L, wait));
            }
            else
            {
                hasUnknownWait = true;
            }
        }

        return new ResourceUpgradeAffordability(
            hasUnknownWait ? long.MaxValue : longest,
            hasUnknownWait,
            wood + clay + iron + crop);
    }
}

internal sealed record ResourceUpgradeAffordability(
    long TimeUntilAffordableSeconds,
    bool HasUnknownWait,
    long TotalCost);
