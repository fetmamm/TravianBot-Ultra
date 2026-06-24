using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless construction-slot reasoning extracted from <see cref="TravianClient"/>:
/// how many build slots are in use, which kinds can still be started (tribe/Plus rules),
/// and the shortest remaining wait. Pure functions so they can be unit-tested in isolation.
/// </summary>
internal static class ConstructionSlots
{
    internal static int ActiveBuildCount(
        IReadOnlyList<BuildQueueItem> buildQueue,
        IReadOnlyList<ActiveConstruction> activeConstructions)
    {
        return activeConstructions.Count > 0
            ? activeConstructions.Count
            : buildQueue.Count;
    }

    internal static ConstructionSlotStatus Compute(
        IReadOnlyList<ActiveConstruction> active,
        string tribe,
        bool travianPlusActive)
    {
        var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);
        var resourceUsed = active.Count(a => a.Kind == ConstructionKind.Resource);
        var buildingUsed = active.Count(a => a.Kind != ConstructionKind.Resource);

        bool canResource;
        bool canBuilding;
        int resourceMax;
        int buildingMax;

        if (isRomans)
        {
            resourceMax = 1;
            buildingMax = travianPlusActive ? 2 : 1;
            canResource = resourceUsed < resourceMax;
            canBuilding = buildingUsed < buildingMax;
        }
        else
        {
            resourceMax = 1;
            buildingMax = travianPlusActive ? 2 : 1;
            var totalUsed = active.Count;
            canResource = canBuilding = totalUsed < buildingMax;
        }

        int? shortest = null;
        foreach (var item in active)
        {
            if (item.TimeLeftSeconds is int s && s > 0)
            {
                shortest = shortest is null ? s : Math.Min(shortest.Value, s);
            }
        }

        return new ConstructionSlotStatus(
            Active: active,
            ResourceSlotsUsed: resourceUsed,
            BuildingSlotsUsed: buildingUsed,
            ResourceSlotsMax: resourceMax,
            BuildingSlotsMax: buildingMax,
            CanStartResource: canResource,
            CanStartBuilding: canBuilding,
            ShortestWaitSeconds: shortest);
    }
}
