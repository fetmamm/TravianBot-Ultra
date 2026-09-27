using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

internal sealed record ConstructionQueueReconciliationPlan(
    IReadOnlyList<Guid> Removals,
    IReadOnlyList<QueuePayloadUpdate> Updates,
    IReadOnlyList<BuildingConstructSlotConflictReconciliation> SlotConflicts)
{
    public bool HasChanges => Removals.Count > 0 || Updates.Count > 0;
}

internal static class ConstructionQueueReconciliation
{
    public static ConstructionQueueReconciliationPlan Plan(VillageStatus status, IReadOnlyList<QueueItem> sameVillageItems)
    {
        var candidates = sameVillageItems.Where(item => item.Status == QueueStatus.Pending).ToList();
        var removals = new HashSet<Guid>();
        var updates = new Dictionary<Guid, QueuePayloadUpdate>();

        foreach (var candidate in candidates.ToList())
        {
            if (!string.Equals(candidate.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                || !ResourceUpgradePayload.TryFromDictionary(candidate.Payload, out var resourceUpgrade, int.MaxValue)
                || resourceUpgrade is null)
            {
                continue;
            }

            var liveField = status.ResourceFields.FirstOrDefault(field => field.SlotId == resourceUpgrade.SlotId);
            if (liveField?.Level is not int liveLevel || liveLevel < resourceUpgrade.TargetLevel)
            {
                continue;
            }

            removals.Add(candidate.Id);
            candidates.Remove(candidate);
        }

        foreach (var candidate in candidates.ToList())
        {
            var construct = BuildingUpgradeSlotRebindPlanner.FindExistingConstruct(status, candidate);
            if (construct is null) continue;
            if (BuildingConstructPayload.TryFromDictionary(candidate.Payload, out var constructPayload)
                && constructPayload is not null
                && construct.LiveLevel < constructPayload.TargetLevel)
            {
                if (construct.LiveSlotId != construct.QueuedSlotId)
                {
                    var payload = new Dictionary<string, string>(candidate.Payload, StringComparer.OrdinalIgnoreCase)
                    {
                        [BotOptionPayloadKeys.BuildingConstructSlotId] = construct.LiveSlotId.ToString(),
                    };
                    updates[candidate.Id] = new QueuePayloadUpdate(candidate.Id, payload);
                }
                continue;
            }
            foreach (var rebind in BuildingUpgradeSlotRebindPlanner.Plan(candidate, construct.LiveSlotId, candidates))
            {
                updates[rebind.QueueItemId] = new QueuePayloadUpdate(rebind.QueueItemId, rebind.Payload);
            }
            removals.Add(candidate.Id);
            candidates.Remove(candidate);
        }

        foreach (var reconciliation in BuildingUpgradeSlotRebindPlanner.PlanFromLiveStatus(status, candidates))
        {
            if (reconciliation.TargetSatisfied)
            {
                removals.Add(reconciliation.QueueItemId);
                continue;
            }
            updates[reconciliation.QueueItemId] = new QueuePayloadUpdate(reconciliation.QueueItemId, reconciliation.Payload);
        }

        var assignedFallbackSlots = new HashSet<int>();
        var slotConflicts = new List<BuildingConstructSlotConflictReconciliation>();
        foreach (var candidate in candidates
                     .Where(item => !removals.Contains(item.Id) && !updates.ContainsKey(item.Id))
                     .OrderByDescending(item => item.Priority)
                     .ThenBy(item => item.CreatedAt))
        {
            var conflict = BuildingUpgradeSlotRebindPlanner.PlanConstructSlotConflict(
                status,
                candidate,
                candidates,
                assignedFallbackSlots);
            if (conflict?.ReboundSlotId is not int reboundSlotId)
            {
                continue;
            }

            slotConflicts.Add(conflict);
            assignedFallbackSlots.Add(reboundSlotId);
            foreach (var update in conflict.Updates)
            {
                if (!removals.Contains(update.QueueItemId))
                {
                    updates[update.QueueItemId] = update;
                }
            }
        }

        return new ConstructionQueueReconciliationPlan(
            removals.ToList(),
            updates.Values.Where(update => !removals.Contains(update.QueueItemId)).ToList(),
            slotConflicts);
    }
}
