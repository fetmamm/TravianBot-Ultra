using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed record MainBuildingRebuildPlan(int SlotId, int TargetLevel);

public static class MainBuildingRebuildPlanner
{
    private const int MainBuildingGid = 15;

    public static MainBuildingRebuildPlan? Plan(
        VillageStatus liveStatus,
        VillageStatus? previousStatus,
        IReadOnlyList<QueueItem> sameVillageItems,
        int targetLevel)
    {
        if (!BuildingUpgradeSlotRebindPlanner.HasCompleteBuildingOverview(liveStatus)
            || liveStatus.Buildings.Any(building => IsMainBuilding(building) && (building.Level ?? 0) > 0)
            || sameVillageItems.Any(IsActiveMainBuildingConstruct))
        {
            return null;
        }

        var emptySlots = BuildingUpgradeSlotRebindPlanner.GetConfirmedEmptyOrdinarySlotIds(liveStatus);
        var reservedSlots = sameVillageItems
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
                && string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && BuildingConstructPayload.TryFromDictionary(item.Payload, out _))
            .Select(item => BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
                ? payload?.SlotId
                : null)
            .Where(slot => slot.HasValue)
            .Select(slot => slot!.Value)
            .ToHashSet();

        var reportedMainBuildingSlot = liveStatus.Buildings
            .Where(building => IsMainBuilding(building) && (building.Level ?? 0) == 0)
            .Select(building => building.SlotId)
            .FirstOrDefault(slot => slot is >= 19 and <= 38);
        var previousMainBuildingSlot = previousStatus?.Buildings
            .Where(IsMainBuilding)
            .Select(building => building.SlotId)
            .FirstOrDefault(slot => slot is >= 19 and <= 38);
        var slotId = new[] { reportedMainBuildingSlot, previousMainBuildingSlot }
            .Where(slot => slot.HasValue && emptySlots.Contains(slot.Value) && !reservedSlots.Contains(slot.Value))
            .Select(slot => slot!.Value)
            .Cast<int?>()
            .FirstOrDefault()
            ?? emptySlots.Where(slot => !reservedSlots.Contains(slot)).Cast<int?>().FirstOrDefault();

        return slotId.HasValue
            ? new MainBuildingRebuildPlan(
                slotId.Value,
                ConstructionDefaults.NormalizeMainBuildingRebuildTargetLevel(targetLevel))
            : null;
    }

    private static bool IsMainBuilding(Building building)
        => (building.Gid ?? BuildingCatalogService.GidForName(building.Name)) == MainBuildingGid;

    private static bool IsActiveMainBuildingConstruct(QueueItem item)
        => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
            && string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
            && payload?.Gid == MainBuildingGid;
}
