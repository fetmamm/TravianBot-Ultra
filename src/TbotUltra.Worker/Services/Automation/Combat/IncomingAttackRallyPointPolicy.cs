namespace TbotUltra.Worker.Services;

internal enum RallyPointConstructionState
{
    Unknown,
    Constructed,
    Missing,
}

/// <summary>
/// Classifies the fixed Rally Point slot from a hydrated village building overview.
/// </summary>
internal static class IncomingAttackRallyPointPolicy
{
    private const int RallyPointSlotId = 39;

    internal static RallyPointConstructionState GetConstructionState(BuildingOverviewScanResult scan)
    {
        if (scan.Buildings.Values.Any(building =>
                BuildingOverviewDomParser.ParseGidFromBuildingCode(building.BuildingCode) == 16
                || BuildingNames.Same(building.BuildingName, "Rally Point")))
        {
            return RallyPointConstructionState.Constructed;
        }

        if (scan.Metrics.SlotCount < 18)
        {
            return RallyPointConstructionState.Unknown;
        }

        // Some Travian layouts omit the special fixed slot from div.buildingSlot while it is empty.
        // A hydrated overview with no Rally Point anywhere is therefore authoritative enough to
        // classify the absent slot as missing instead of repeatedly probing build.php?gid=16.
        if (!scan.Buildings.TryGetValue(RallyPointSlotId, out var fixedSlot))
        {
            return RallyPointConstructionState.Missing;
        }

        if (!string.IsNullOrWhiteSpace(fixedSlot.BuildingCode)
            || fixedSlot.Level > 0
            || (!BuildingNames.Same(fixedSlot.BuildingName, "Empty")
                && !BuildingNames.Same(fixedSlot.BuildingName, "Unknown building")))
        {
            return RallyPointConstructionState.Unknown;
        }

        return RallyPointConstructionState.Missing;
    }
}
