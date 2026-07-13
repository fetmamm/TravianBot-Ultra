namespace TbotUltra.Worker.Services;

internal enum BuildingOverviewScanConfidence
{
    Low,
    Medium,
    High,
}

internal sealed record BuildingOverviewScanMetrics(
    int SlotCount,
    BuildingOverviewScanConfidence Confidence,
    int MissingBuildingCodeCount,
    int UnknownLevelCount,
    bool MissingMainBuilding,
    bool MissingRallyPoint);

/// <summary>
/// Stateless quality decisions for building-overview snapshots. Browser navigation and DOM
/// extraction stay in <see cref="TravianClient"/>; this policy only decides whether a snapshot
/// is useful and which of two snapshots contains stronger evidence.
/// </summary>
internal static class BuildingOverviewScanPolicy
{
    internal static BuildingOverviewScanMetrics Evaluate(
        int slotCount,
        int missingBuildingCodeCount,
        int unknownLevelCount,
        bool hasMainBuilding,
        bool hasRallyPoint)
    {
        var confidence = BuildingOverviewScanConfidence.High;
        if (slotCount < 18
            || !hasMainBuilding
            || missingBuildingCodeCount >= 3
            || unknownLevelCount >= 3)
        {
            confidence = BuildingOverviewScanConfidence.Low;
        }
        else if (slotCount < 22
            || missingBuildingCodeCount > 0
            || unknownLevelCount > 0
            || !hasRallyPoint)
        {
            confidence = BuildingOverviewScanConfidence.Medium;
        }

        return new BuildingOverviewScanMetrics(
            slotCount,
            confidence,
            missingBuildingCodeCount,
            unknownLevelCount,
            MissingMainBuilding: !hasMainBuilding,
            MissingRallyPoint: !hasRallyPoint);
    }

    internal static bool ShouldRetry(BuildingOverviewScanMetrics scan)
    {
        // A partial snapshot can still be enough for a caller targeting one known slot.
        // Reload only when the overview is unusable or lacks its strongest identity signal.
        return scan.SlotCount < 18 || scan.MissingMainBuilding;
    }

    internal static bool PreferSecond(
        BuildingOverviewScanMetrics first,
        BuildingOverviewScanMetrics second)
    {
        return Score(second) >= Score(first);
    }

    internal static string Describe(BuildingOverviewScanMetrics scan)
    {
        return $"slots={scan.SlotCount}, missing_gid={scan.MissingBuildingCodeCount}, unknown_level={scan.UnknownLevelCount}, main={(scan.MissingMainBuilding ? "missing" : "ok")}, rally={(scan.MissingRallyPoint ? "missing" : "ok")}";
    }

    private static int Score(BuildingOverviewScanMetrics scan)
    {
        var baseScore = scan.Confidence switch
        {
            BuildingOverviewScanConfidence.High => 300,
            BuildingOverviewScanConfidence.Medium => 200,
            _ => 100,
        };

        return baseScore
            + (scan.SlotCount * 5)
            - (scan.MissingBuildingCodeCount * 20)
            - (scan.UnknownLevelCount * 20)
            - (scan.MissingMainBuilding ? 60 : 0)
            - (scan.MissingRallyPoint ? 20 : 0);
    }
}
