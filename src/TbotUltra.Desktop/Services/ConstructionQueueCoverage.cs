using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class ConstructionQueueCoverage
{
    public static int? ResolveActiveCoveredLevel(QueueItem item, VillageStatus? status)
    {
        if (item.Status is not (QueueStatus.Pending or QueueStatus.Paused)
            || !string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            || status?.ActiveConstructionsFromOverview != true
            || !TryReadPositiveInt(item.Payload, BotOptionPayloadKeys.BuildingUpgradeSlotId, out var slotId)
            || !TryReadPositiveInt(item.Payload, BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var targetLevel))
        {
            return null;
        }

        var coveredLevel = ConstructionQueueState.ResolveCurrentActiveConstructions(status)
            .Where(active => active.Kind != ConstructionKind.Resource
                && active.SlotId == slotId
                && active.Level is not null)
            .Select(active => active.Level!.Value)
            .DefaultIfEmpty(0)
            .Max();

        return coveredLevel >= targetLevel ? coveredLevel : null;
    }

    private static bool TryReadPositiveInt(
        IReadOnlyDictionary<string, string> payload,
        string key,
        out int value)
    {
        value = 0;
        return payload.TryGetValue(key, out var text)
            && int.TryParse(text, out value)
            && value > 0;
    }
}
