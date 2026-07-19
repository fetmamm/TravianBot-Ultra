using System;
using System.Collections.Generic;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Pure catalog cost/time summation for queued construction estimates, extracted from
/// MainWindow.QueueUi.Estimates code-behind. Returns an alarm reason instead of raising
/// the alarm so the caller keeps the existing one-time alarm behavior; UI/village state
/// resolution (coverage keys, current levels) stays with the caller.
/// </summary>
public static class QueueItemEstimateCalculator
{
    /// <summary>
    /// Sums catalog cost/time across the inclusive level range [fromLevel, toLevel].
    /// Returns (None, reason) when the gid is unknown or catalog data is missing for a level.
    /// </summary>
    public static (QueueItemEstimate Estimate, string? AlarmReason) SumLevels(
        int? gid,
        int fromLevel,
        int toLevel,
        double serverSpeed,
        int mainBuildingLevel)
    {
        if (gid is null)
        {
            return (QueueItemEstimate.None, "building could not be matched to the catalog");
        }

        if (toLevel < fromLevel)
        {
            // Already at/above target: nothing left to build.
            return (new QueueItemEstimate(true, 0, 0, 0, 0, 0), null);
        }

        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        for (var level = fromLevel; level <= toLevel; level++)
        {
            var stats = BuildingCatalogService.CostFor(gid.Value, level);
            if (stats is null)
            {
                return (QueueItemEstimate.None, $"missing catalog data for gid {gid.Value} level {level}");
            }

            seconds += BuildingCatalogService.BuildSecondsFor(gid.Value, level, serverSpeed, mainBuildingLevel);
            wood += stats.Wood;
            clay += stats.Clay;
            iron += stats.Iron;
            crop += stats.Crop;
        }

        return (new QueueItemEstimate(true, seconds, wood, clay, iron, crop), null);
    }

    /// <summary>
    /// Coverage-aware variant for progressive upgrades of the same building/field. queuedCoverage
    /// carries the highest target already covered by earlier queued items for the coverage key, so
    /// each item only counts its own step. A null coverage key skips coverage bookkeeping.
    /// </summary>
    public static (QueueItemEstimate Estimate, string? AlarmReason) SumLevelsWithQueueCoverage(
        int? gid,
        int? currentLevel,
        int target,
        double serverSpeed,
        int mainBuildingLevel,
        string? coverageKey,
        Dictionary<string, int> queuedCoverage)
    {
        int? floorLevel = currentLevel;
        if (coverageKey is not null && queuedCoverage.TryGetValue(coverageKey, out var covered))
        {
            floorLevel = floorLevel.HasValue ? Math.Max(floorLevel.Value, covered) : covered;
        }

        // Known floor (live level or earlier queued target) -> only the next step(s); unknown floor falls
        // back to the existing behavior of estimating just the target level.
        var fromLevel = floorLevel.HasValue ? floorLevel.Value + 1 : target;
        var result = SumLevels(gid, fromLevel, target, serverSpeed, mainBuildingLevel);

        if (coverageKey is not null)
        {
            queuedCoverage[coverageKey] = Math.Max(queuedCoverage.GetValueOrDefault(coverageKey), target);
        }

        return result;
    }
}
