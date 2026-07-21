using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

internal sealed record BuildingUpgradeSlotRebind(Guid QueueItemId, Dictionary<string, string> Payload);

internal sealed record BuildingUpgradeLiveReconciliation(
    Guid QueueItemId,
    string BuildingName,
    int QueuedSlotId,
    int LiveSlotId,
    int LiveLevel,
    int? TargetLevel,
    bool TargetSatisfied,
    Dictionary<string, string> Payload);

internal sealed record BuildingConstructLiveMatch(
    Guid QueueItemId,
    string BuildingName,
    int QueuedSlotId,
    int LiveSlotId,
    int LiveLevel);

internal static class BuildingUpgradeSlotRebindPlanner
{
    public static IReadOnlyList<BuildingUpgradeLiveReconciliation> PlanFromLiveStatus(
        VillageStatus status,
        IReadOnlyList<QueueItem> sameVillageItems)
    {
        var result = new List<BuildingUpgradeLiveReconciliation>();
        foreach (var candidate in sameVillageItems.Where(item =>
                     item.Status == QueueStatus.Pending
                     && (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))))
        {
            if (PlanUpgradeFromLiveStatus(status, candidate) is { } reconciliation)
            {
                if (reconciliation.TargetSatisfied
                    || reconciliation.LiveSlotId != reconciliation.QueuedSlotId)
                {
                    result.Add(reconciliation);
                }
            }
        }

        return result;
    }

    public static BuildingUpgradeLiveReconciliation? PlanUpgradeFromLiveStatus(
        VillageStatus status,
        QueueItem candidate)
    {
        if (!BuildingUpgradePayload.TryFromDictionary(candidate.Payload, out var upgrade)
            || upgrade is null
            || BuildingCatalogService.GidForName(upgrade.Name) is not int gid
            || !BuildingCatalogService.IsSingleInstance(gid))
        {
            return null;
        }

        var liveMatches = FindLiveMatches(status, gid);
        if (liveMatches.Count != 1
            || liveMatches[0].SlotId is not int liveSlotId
            || liveMatches[0].Level is not int liveLevel)
        {
            return null;
        }

        var targetLevel = upgrade.TargetLevel;
        if (string.Equals(candidate.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            targetLevel = BuildingCatalogService.MaxLevelFor(gid);
        }

        var targetSatisfied = targetLevel is int target && liveLevel >= target;
        var payload = new Dictionary<string, string>(candidate.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = liveSlotId.ToString(),
        };
        return new BuildingUpgradeLiveReconciliation(
            candidate.Id,
            upgrade.Name ?? liveMatches[0].Name,
            upgrade.SlotId,
            liveSlotId,
            liveLevel,
            targetLevel,
            targetSatisfied,
            payload);
    }

    public static BuildingConstructLiveMatch? FindExistingConstruct(
        VillageStatus status,
        QueueItem candidate)
    {
        if (!string.Equals(candidate.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(candidate.Payload, out var construct)
            || construct is null
            || !BuildingCatalogService.IsSingleInstance(construct.Gid))
        {
            return null;
        }

        var liveMatches = FindLiveMatches(status, construct.Gid);
        if (liveMatches.Count != 1
            || liveMatches[0].SlotId is not int liveSlotId
            || liveMatches[0].Level is not int liveLevel)
        {
            return null;
        }

        return new BuildingConstructLiveMatch(
            candidate.Id,
            construct.Name ?? liveMatches[0].Name,
            construct.SlotId,
            liveSlotId,
            liveLevel);
    }

    public static bool HasLiveBuildingIdentity(VillageStatus status, int gid)
        => status.Buildings.Any(building => building.SlotId is >= 19 and <= 40
            && (building.Gid ?? BuildingCatalogService.GidForName(building.Name)) == gid);

    public static bool HasCompleteBuildingOverview(VillageStatus status)
        => status.Buildings
            .Where(building => building.SlotId is >= 19 and <= 40)
            .Select(building => building.SlotId)
            .Distinct()
            .Count() == 22;

    public static IReadOnlyList<BuildingUpgradeSlotRebind> Plan(
        QueueItem sourceConstruct,
        int effectiveSlotId,
        IReadOnlyList<QueueItem> sameVillageItems)
    {
        if (effectiveSlotId is < 19 or > 38
            || !BuildingConstructPayload.TryFromDictionary(sourceConstruct.Payload, out var construct)
            || construct is null
            || construct.SlotId == effectiveSlotId
            || !BuildingCatalogService.IsSingleInstance(construct.Gid))
        {
            return [];
        }

        var result = new List<BuildingUpgradeSlotRebind>();
        foreach (var candidate in sameVillageItems.Where(item =>
                     item.Id != sourceConstruct.Id
                     && item.Status == QueueStatus.Pending
                     && (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))))
        {
            if (!BuildingUpgradePayload.TryFromDictionary(candidate.Payload, out var upgrade)
                || upgrade is null
                || upgrade.SlotId != construct.SlotId
                || !MatchesConstructedBuilding(upgrade.Name, construct))
            {
                continue;
            }

            var payload = new Dictionary<string, string>(candidate.Payload, StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = effectiveSlotId.ToString(),
            };
            result.Add(new BuildingUpgradeSlotRebind(candidate.Id, payload));
        }

        return result;
    }

    private static bool MatchesConstructedBuilding(string? upgradeName, BuildingConstructPayload construct)
    {
        if (BuildingCatalogService.GidForName(upgradeName) is int upgradeGid)
        {
            return upgradeGid == construct.Gid;
        }

        return !string.IsNullOrWhiteSpace(upgradeName)
            && !string.IsNullOrWhiteSpace(construct.Name)
            && string.Equals(upgradeName.Trim(), construct.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static List<Building> FindLiveMatches(VillageStatus status, int gid)
        => status.Buildings
            .Where(building => building.SlotId is >= 19 and <= 40
                && (building.Level ?? 0) >= 1
                && (building.Gid ?? BuildingCatalogService.GidForName(building.Name)) == gid)
            .ToList();
}
