using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal static class ResourceConstructionQueueMatcher
{
    internal static int HighestQueuedLevelForSlot(
        IReadOnlyList<ActiveConstruction> activeConstructions,
        int slotId,
        string resourceName,
        int currentLevel)
    {
        var highestQueuedLevel = MatchForResourceSlot(activeConstructions, slotId, resourceName)
            .Select(item => item.Level ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(currentLevel, highestQueuedLevel);
    }

    internal static IReadOnlyList<ActiveConstruction> MatchForResourceSlot(
        IReadOnlyList<ActiveConstruction> activeConstructions,
        int? slotId,
        string resourceName)
    {
        return activeConstructions
            .Where(item => IsMatch(item, slotId, resourceName))
            .ToList();
    }

    private static bool IsMatch(ActiveConstruction item, int? slotId, string resourceName)
    {
        if (item.Kind != ConstructionKind.Resource)
        {
            return false;
        }

        if (slotId is int requestedSlot && requestedSlot > 0)
        {
            if (item.SlotId is int activeSlot)
            {
                return activeSlot == requestedSlot;
            }

            return BuildingNames.Same(item.Name, resourceName);
        }

        return string.IsNullOrWhiteSpace(resourceName) || BuildingNames.Same(item.Name, resourceName);
    }
}
