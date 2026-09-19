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

internal sealed record BuildingConstructSlotConflictReconciliation(
    Guid QueueItemId,
    string BuildingName,
    int QueuedSlotId,
    string OccupyingBuildingName,
    int? ReboundSlotId,
    IReadOnlyList<int> ConfirmedEmptySlotIds,
    IReadOnlyList<QueuePayloadUpdate> Updates);

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
            var hasActiveConstructForQueuedSlot = HasActiveConstructForUpgrade(candidate, sameVillageItems);
            if (PlanUpgradeFromLiveStatus(
                    status,
                    candidate,
                    allowMultiInstanceFallback: !hasActiveConstructForQueuedSlot) is { } reconciliation)
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
        QueueItem candidate,
        bool allowMultiInstanceFallback = true)
    {
        if (!BuildingUpgradePayload.TryFromDictionary(candidate.Payload, out var upgrade)
            || upgrade is null
            || BuildingCatalogService.GidForName(upgrade.Name) is not int gid)
        {
            return null;
        }

        var targetLevel = upgrade.TargetLevel;
        if (string.Equals(candidate.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            targetLevel = BuildingCatalogService.MaxLevelFor(gid);
        }

        var liveMatches = FindLiveMatches(status, gid);
        var liveMatch = liveMatches.FirstOrDefault(building => building.SlotId == upgrade.SlotId);
        if (liveMatch is null && BuildingCatalogService.IsSingleInstance(gid) && liveMatches.Count == 1)
        {
            liveMatch = liveMatches[0];
        }
        else if (liveMatch is null
                 && allowMultiInstanceFallback
                 && targetLevel is int candidateTarget)
        {
            // Multi-instance buildings cannot normally move between slots. A stale queued slot can still
            // be repaired safely when exactly one live instance remains below this task's target; instances
            // already at/above target are not candidates. Never guess between multiple unfinished copies.
            var unfinishedMatches = liveMatches
                .Where(building => building.Level is int level && level < candidateTarget)
                .ToList();
            if (unfinishedMatches.Count == 1)
            {
                liveMatch = unfinishedMatches[0];
            }
        }

        if (liveMatch?.SlotId is not int liveSlotId
            || liveMatch.Level is not int liveLevel)
        {
            return null;
        }

        var targetSatisfied = targetLevel is int target && liveLevel >= target;
        var payload = new Dictionary<string, string>(candidate.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = liveSlotId.ToString(),
        };
        return new BuildingUpgradeLiveReconciliation(
            candidate.Id,
            upgrade.Name ?? liveMatch.Name,
            upgrade.SlotId,
            liveSlotId,
            liveLevel,
            targetLevel,
            targetSatisfied,
            payload);
    }

    private static bool HasActiveConstructForUpgrade(
        QueueItem upgradeItem,
        IReadOnlyList<QueueItem> sameVillageItems)
    {
        if (!BuildingUpgradePayload.TryFromDictionary(upgradeItem.Payload, out var upgrade)
            || upgrade is null
            || BuildingCatalogService.GidForName(upgrade.Name) is not int upgradeGid)
        {
            return false;
        }

        return sameVillageItems.Any(item =>
            item.Id != upgradeItem.Id
            && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
            && string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
            && construct is not null
            && construct.SlotId == upgrade.SlotId
            && construct.Gid == upgradeGid);
    }

    public static BuildingConstructLiveMatch? FindExistingConstruct(
        VillageStatus status,
        QueueItem candidate)
    {
        if (!string.Equals(candidate.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(candidate.Payload, out var construct)
            || construct is null)
        {
            return null;
        }

        var liveMatches = FindLiveMatches(status, construct.Gid);
        var exactSlotMatch = liveMatches.FirstOrDefault(building => building.SlotId == construct.SlotId);
        var existing = exactSlotMatch;
        if (existing is null && BuildingCatalogService.IsSingleInstance(construct.Gid))
        {
            existing = liveMatches.FirstOrDefault();
        }

        if (existing?.SlotId is not int liveSlotId
            || existing.Level is not int liveLevel)
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

    public static BuildingConstructSlotConflictReconciliation? PlanConstructSlotConflict(
        VillageStatus status,
        QueueItem sourceConstruct,
        IReadOnlyList<QueueItem> sameVillageItems,
        IReadOnlySet<int>? additionallyReservedSlots = null)
    {
        if (!HasCompleteBuildingOverview(status)
            || !string.Equals(sourceConstruct.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(sourceConstruct.Payload, out var construct)
            || construct is null
            || construct.SlotId is < 19 or > 38)
        {
            return null;
        }

        var targetSlot = status.Buildings.FirstOrDefault(building => building.SlotId == construct.SlotId);
        if (targetSlot is null || IsConfirmedEmptyOrdinarySlot(targetSlot))
        {
            return null;
        }

        var targetGid = targetSlot.Gid ?? BuildingCatalogService.GidForName(targetSlot.Name);
        if (targetGid == construct.Gid)
        {
            return null;
        }

        // Single-instance buildings that already exist elsewhere are handled by the normal
        // existing-construct reconciliation. Moving them to another empty slot would create
        // an impossible duplicate instead of repairing the queued intent.
        if (BuildingCatalogService.IsSingleInstance(construct.Gid)
            && FindExistingConstruct(status, sourceConstruct) is not null)
        {
            return null;
        }

        var reservedSlots = sameVillageItems
            .Where(item => item.Id != sourceConstruct.Id
                && string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .Select(item => BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
                ? payload?.SlotId
                : null)
            .Where(slot => slot is >= 19 and <= 38)
            .Select(slot => slot!.Value)
            .ToHashSet();
        if (additionallyReservedSlots is not null)
        {
            reservedSlots.UnionWith(additionallyReservedSlots);
        }

        if (sourceConstruct.Payload.TryGetValue(
                BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots,
                out var excludedSlotsRaw))
        {
            reservedSlots.UnionWith(ParseOrdinarySlotIds(excludedSlotsRaw));
        }

        var confirmedEmptySlotIds = GetConfirmedEmptyOrdinarySlotIds(status);
        var reboundSlotId = confirmedEmptySlotIds
            .Where(slot => !reservedSlots.Contains(slot))
            .Cast<int?>()
            .FirstOrDefault();
        if (reboundSlotId is null)
        {
            return new BuildingConstructSlotConflictReconciliation(
                sourceConstruct.Id,
                construct.Name ?? $"gid {construct.Gid}",
                construct.SlotId,
                targetSlot.Name,
                null,
                confirmedEmptySlotIds,
                []);
        }

        var sourcePayload = new Dictionary<string, string>(sourceConstruct.Payload, StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingConstructSlotId] = reboundSlotId.Value.ToString(),
        };
        var updates = new List<QueuePayloadUpdate>
        {
            new(sourceConstruct.Id, sourcePayload),
        };
        updates.AddRange(Plan(sourceConstruct, reboundSlotId.Value, sameVillageItems)
            .Select(rebind => new QueuePayloadUpdate(rebind.QueueItemId, rebind.Payload)));

        return new BuildingConstructSlotConflictReconciliation(
            sourceConstruct.Id,
            construct.Name ?? $"gid {construct.Gid}",
            construct.SlotId,
            targetSlot.Name,
            reboundSlotId,
            confirmedEmptySlotIds,
            updates);
    }

    public static IReadOnlyList<BuildingUpgradeSlotRebind> Plan(
        QueueItem sourceConstruct,
        int effectiveSlotId,
        IReadOnlyList<QueueItem> sameVillageItems)
    {
        if (effectiveSlotId is < 19 or > 38
            || !BuildingConstructPayload.TryFromDictionary(sourceConstruct.Payload, out var construct)
            || construct is null
            || construct.SlotId == effectiveSlotId)
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

    private static bool IsConfirmedEmptyOrdinarySlot(Building building)
        => building.SlotId is >= 19 and <= 38
            && (building.Level ?? 0) == 0
            && (building.Gid ?? 0) == 0
            && string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<int> GetConfirmedEmptyOrdinarySlotIds(VillageStatus status)
        => status.Buildings
            .Where(IsConfirmedEmptyOrdinarySlot)
            .Select(building => building.SlotId!.Value)
            .OrderBy(slot => slot)
            .ToList();

    private static IEnumerable<int> ParseOrdinarySlotIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => int.TryParse(item, out var slot) ? slot : 0)
                .Where(slot => slot is >= 19 and <= 38);

    private static List<Building> FindLiveMatches(VillageStatus status, int gid)
        => status.Buildings
            .Where(building => building.SlotId is >= 19 and <= 40
                && (building.Level ?? 0) >= 1
                && (building.Gid ?? BuildingCatalogService.GidForName(building.Name)) == gid)
            .ToList();
}
