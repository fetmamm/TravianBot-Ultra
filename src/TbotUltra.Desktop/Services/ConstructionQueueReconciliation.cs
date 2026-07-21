using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

internal sealed record ConstructionQueueReconciliationPlan(
    IReadOnlyList<Guid> Removals,
    IReadOnlyList<QueuePayloadUpdate> Updates)
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
            var construct = BuildingUpgradeSlotRebindPlanner.FindExistingConstruct(status, candidate);
            if (construct is null) continue;
            foreach (var rebind in BuildingUpgradeSlotRebindPlanner.Plan(candidate, construct.LiveSlotId, candidates))
            {
                updates[rebind.QueueItemId] = new QueuePayloadUpdate(rebind.QueueItemId, rebind.Payload);
                candidates.First(item => item.Id == rebind.QueueItemId).Payload = rebind.Payload;
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

        return new ConstructionQueueReconciliationPlan(
            removals.ToList(),
            updates.Values.Where(update => !removals.Contains(update.QueueItemId)).ToList());
    }
}
