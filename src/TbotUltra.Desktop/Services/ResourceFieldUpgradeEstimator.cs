using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public readonly record struct ResourceFieldUpgradeEstimate(
    double Seconds,
    long Wood,
    long Clay,
    long Iron,
    long Crop);

public static class ResourceFieldUpgradeEstimator
{
    private const int ExpectedResourceFieldCount = 18;

    public static bool TryEstimate(
        IReadOnlyList<ResourceFieldRow> fields,
        int targetLevel,
        double serverSpeed,
        int mainBuildingLevel,
        out ResourceFieldUpgradeEstimate estimate,
        out string? failureReason)
    {
        estimate = default;
        failureReason = null;

        if (fields.Count != ExpectedResourceFieldCount
            || fields.Select(field => field.SlotId).Distinct().Count() != ExpectedResourceFieldCount)
        {
            failureReason = $"expected {ExpectedResourceFieldCount} resource fields but found {fields.Count}";
            return false;
        }

        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        foreach (var field in fields)
        {
            if (field.Level is not int currentLevel || currentLevel < 0)
            {
                failureReason = $"resource slot {field.SlotId} has no current level";
                return false;
            }

            var gid = BuildingCatalogService.GidForName(field.Name)
                ?? BuildingCatalogService.GidForName(field.FieldType);
            if (gid is null)
            {
                failureReason = $"resource slot {field.SlotId} could not be matched to the catalog";
                return false;
            }

            for (var level = currentLevel + 1; level <= targetLevel; level++)
            {
                var stats = BuildingCatalogService.CostFor(gid.Value, level);
                if (stats is null)
                {
                    failureReason = $"missing catalog data for gid {gid.Value} level {level}";
                    return false;
                }

                seconds += BuildingCatalogService.BuildSecondsFor(
                    gid.Value,
                    level,
                    serverSpeed,
                    mainBuildingLevel);
                wood += stats.Wood;
                clay += stats.Clay;
                iron += stats.Iron;
                crop += stats.Crop;
            }
        }

        estimate = new ResourceFieldUpgradeEstimate(seconds, wood, clay, iron, crop);
        return true;
    }
}
