using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal static class ResourceConstructionQueueMatcher
{
    internal static int HighestQueuedLevelForSlot(
        IReadOnlyList<ActiveConstruction> activeConstructions,
        int slotId,
        string resourceName,
        int currentLevel,
        bool allowUnknownSlotNameFallback = true)
    {
        var highestQueuedLevel = activeConstructions
            .Where(item => IsMatch(item, slotId, resourceName, allowUnknownSlotNameFallback))
            .Select(item => item.Level ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(currentLevel, highestQueuedLevel);
    }

    internal static int HighestQueuedLevelForSlot(
        IReadOnlyList<BuildQueueItem> buildQueue,
        int slotId,
        string resourceName,
        int currentLevel,
        bool allowUnknownSlotNameFallback = true)
    {
        var matchingLevels = buildQueue
            .Where(item =>
                item.SlotId == slotId
                || (allowUnknownSlotNameFallback
                    && item.SlotId is null
                    && BuildQueueFingerprints.TextMatchesBuilding(item.Text, resourceName)))
            .Select(item => BuildQueueFingerprints.TryReadLevel(item.Text) ?? 0);
        return Math.Max(currentLevel, matchingLevels.DefaultIfEmpty(0).Max());
    }

    internal static int HighestQueuedLevelForSlot(
        IReadOnlyList<ActiveConstruction> activeConstructions,
        IReadOnlyList<BuildQueueItem> buildQueue,
        int slotId,
        string resourceName,
        int currentLevel)
    {
        var exactLevel = Math.Max(
            HighestQueuedLevelForSlot(activeConstructions, slotId, resourceName, currentLevel, false),
            HighestQueuedLevelForSlot(buildQueue, slotId, resourceName, currentLevel, false));
        if (exactLevel > currentLevel)
        {
            return exactLevel;
        }

        var hasExactSlotIdentity = activeConstructions.Any(item =>
                item.Kind == ConstructionKind.Resource && item.SlotId == slotId)
            || buildQueue.Any(item => item.SlotId == slotId);
        if (hasExactSlotIdentity)
        {
            // Exact queue identity makes an unknown-slot same-name level safe as supplemental data.
            return Math.Max(
                HighestQueuedLevelForSlot(activeConstructions, slotId, resourceName, currentLevel),
                HighestQueuedLevelForSlot(buildQueue, slotId, resourceName, currentLevel));
        }

        // Resource names repeat across many fields. If either queue source identifies a same-name
        // construction by another slot, an unknown-slot same-name row cannot safely represent this slot.
        var hasKnownSameNameSlot = activeConstructions.Any(item =>
                item.Kind == ConstructionKind.Resource
                && item.SlotId is not null
                && BuildingNames.Same(item.Name, resourceName))
            || buildQueue.Any(item =>
                item.SlotId is not null
                && BuildQueueFingerprints.TextMatchesBuilding(item.Text, resourceName));
        if (hasKnownSameNameSlot)
        {
            return currentLevel;
        }

        return Math.Max(
            HighestQueuedLevelForSlot(activeConstructions, slotId, resourceName, currentLevel),
            HighestQueuedLevelForSlot(buildQueue, slotId, resourceName, currentLevel));
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

    private static bool IsMatch(
        ActiveConstruction item,
        int? slotId,
        string resourceName,
        bool allowUnknownSlotNameFallback = true)
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

            return allowUnknownSlotNameFallback && BuildingNames.Same(item.Name, resourceName);
        }

        return string.IsNullOrWhiteSpace(resourceName) || BuildingNames.Same(item.Name, resourceName);
    }
}
