using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public enum RomanLoginFillState
{
    NotApplicable = 0,
    Complete = 1,
    NeedsComplementaryCategory = 2,
    Blocked = 3,
}

public sealed record RomanLoginFillDecision(
    RomanLoginFillState State,
    int ResourceCount,
    int BuildingCount,
    string Reason);

public static class RomanLoginFillPolicy
{
    public static RomanLoginFillDecision Resolve(
        VillageStatus? status,
        bool? travianPlusActive,
        bool hasPendingResource,
        bool hasPendingBuilding,
        DateTimeOffset? now = null)
    {
        if (status is null
            || !string.Equals(status.Tribe, "Romans", StringComparison.OrdinalIgnoreCase)
            || travianPlusActive != true
            || status.ActiveConstructionsFromOverview != true)
        {
            return new RomanLoginFillDecision(RomanLoginFillState.NotApplicable, 0, 0, "live Roman Plus status unavailable");
        }

        var active = ConstructionQueueState.ResolveCurrentActiveConstructions(status, now);
        var resources = active.Count(item => item.Kind == ConstructionKind.Resource);
        var buildings = active.Count - resources;
        if (active.Count >= 3)
        {
            return new RomanLoginFillDecision(RomanLoginFillState.Complete, resources, buildings, "live queue is 3/3");
        }

        var hasRunnableCategoryQueued = resources == 0
            ? hasPendingResource
            : buildings == 0
                ? hasPendingBuilding
                : hasPendingResource || hasPendingBuilding;
        if (hasRunnableCategoryQueued)
        {
            return new RomanLoginFillDecision(
                RomanLoginFillState.NeedsComplementaryCategory,
                resources,
                buildings,
                "a category that can fill the remaining Roman slot remains queued");
        }

        var missing = resources == 0
            ? "resource queue item"
            : buildings == 0
                ? "building queue item"
                : "construction queue item";
        return new RomanLoginFillDecision(
            RomanLoginFillState.Blocked,
            resources,
            buildings,
            $"no pending {missing}");
    }
}
