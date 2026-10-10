using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal static class ConstructionStatusSyncPolicy
{
    internal static bool IsCompleteFinalVisit(
        VillageStatus status,
        int? targetCoordX,
        int? targetCoordY,
        int villageNumber,
        int villageCount)
    {
        if (villageNumber != villageCount
            || !status.ActiveConstructionsFromOverview
            || !targetCoordX.HasValue
            || !targetCoordY.HasValue
            || status.ActiveVillageCoordX != targetCoordX
            || status.ActiveVillageCoordY != targetCoordY
            || status.CityStatus == CityStatus.Unknown
            || !VillageStatusCompleteness.HasCompleteResourceFieldSnapshot(status.ResourceFields))
        {
            return false;
        }

        var observedSlots = status.Buildings
            .Where(building => building.SlotId.HasValue)
            .Select(building => building.SlotId!.Value)
            .ToHashSet();
        return BuildingSlotPolicy.OverviewSlots(status.CityStatus)
            .All(observedSlots.Contains);
    }
}
