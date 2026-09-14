using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutomationConstructLiveReconciliationPort
{
    void ReconcileLiveQueue(VillageStatus status);
    void RebindPendingUpgrades(QueueItem source, int liveSlotId);
    bool MarkDeferred(Guid itemId, TimeSpan delay);
    bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload);
    void RebindPendingTemplateStep(QueueItem source, int liveSlotId);
    bool MarkSucceeded(Guid itemId);
    bool RemoveQueueItem(Guid itemId);
    IReadOnlyList<QueueItem> GetSameVillageQueueItems(QueueItem source);
    bool MarkPermanentlyFailed(Guid itemId);
    void ForgetBuildingQueueCaches(QueueItem item);
    bool ApplyPendingQueueReconciliation(IReadOnlyList<QueuePayloadUpdate> updates);
    string? GetVillageName(QueueItem item);
    void RequestQueueUiRefresh();
    void Log(string message);
}

internal interface IAutomationConstructLiveReconciliation
{
    bool TryHandleExistingConstruct(
        QueueItem item,
        VillageStatus freshStatus,
        string logPrefix,
        Stopwatch timer);
    bool TryHandleOccupiedSlot(
        QueueItem item,
        VillageStatus freshStatus,
        string logPrefix,
        Stopwatch timer);
}

internal sealed class AutomationConstructLiveReconciliation(
    IAutomationConstructLiveReconciliationPort port) : IAutomationConstructLiveReconciliation
{
    public bool TryHandleExistingConstruct(
        QueueItem item,
        VillageStatus freshStatus,
        string logPrefix,
        Stopwatch timer)
    {
        port.ReconcileLiveQueue(freshStatus);
        var match = BuildingUpgradeSlotRebindPlanner.FindExistingConstruct(freshStatus, item);
        if (match is null)
        {
            return false;
        }

        port.RebindPendingUpgrades(item, match.LiveSlotId);
        if (BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
            && construct is not null
            && match.LiveLevel < construct.TargetLevel)
        {
            if (match.LiveSlotId == match.QueuedSlotId)
            {
                port.Log(
                    $"[construct-chain] {match.BuildingName} exists at level {match.LiveLevel}; "
                    + $"keeping the same queue item until target level {construct.TargetLevel}.");
                return false;
            }

            if (!port.MarkDeferred(item.Id, TimeSpan.Zero))
            {
                port.Log(
                    $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"could not defer composite construct while rebinding slot "
                    + $"{match.QueuedSlotId} to {match.LiveSlotId}");
                return false;
            }

            var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingConstructSlotId] = match.LiveSlotId.ToString(),
            };
            if (!port.PatchDeferredPayload(item, payload))
            {
                port.Log(
                    $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"could not persist composite construct slot rebind to {match.LiveSlotId}");
                return true;
            }

            item.Payload = payload;
            port.RebindPendingTemplateStep(item, match.LiveSlotId);
            port.RequestQueueUiRefresh();
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"rebound {match.BuildingName} from slot {match.QueuedSlotId} to {match.LiveSlotId}; "
                + $"same item will continue to level {construct.TargetLevel}");
            return true;
        }

        port.ReconcileLiveQueue(freshStatus);
        port.MarkSucceeded(item.Id);
        port.RemoveQueueItem(item.Id);
        port.RequestQueueUiRefresh();
        port.Log(
            $"{logPrefix} SKIP {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
            + $"fresh dorf2 confirms {match.BuildingName} at slot {match.LiveSlotId} level {match.LiveLevel}; "
            + $"removed stale construct for slot {match.QueuedSlotId} before queue/requirement delays");
        return true;
    }

    public bool TryHandleOccupiedSlot(
        QueueItem item,
        VillageStatus freshStatus,
        string logPrefix,
        Stopwatch timer)
    {
        var conflict = BuildingUpgradeSlotRebindPlanner.PlanConstructSlotConflict(
            freshStatus,
            item,
            port.GetSameVillageQueueItems(item));
        if (conflict is null)
        {
            return false;
        }

        if (conflict.ReboundSlotId is not int reboundSlotId)
        {
            var unknownSlots = freshStatus.Buildings
                .Where(building => building.SlotId is >= 19 and <= 38
                    && string.Equals(building.Name, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Select(building => building.SlotId!.Value)
                .Distinct()
                .OrderBy(slot => slot)
                .ToList();
            var villageName = port.GetVillageName(item)
                ?? NormalizeVillageName(freshStatus.ActiveVillage)
                ?? "-";
            var unknownSlotText = unknownSlots.Count == 0
                ? "none"
                : string.Join(", ", unknownSlots);

            if (unknownSlots.Count == 0 && conflict.ConfirmedEmptySlotIds.Count == 0)
            {
                if (!port.MarkPermanentlyFailed(item.Id))
                {
                    port.Log(
                        $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                        + "complete live dorf2 confirmed that every ordinary building slot is occupied, "
                        + "but the task could not be moved to History");
                    return false;
                }

                port.ForgetBuildingQueueCaches(item);
                port.Log(
                    $"ALARM: construction task '{item.TaskName}' in village '{villageName}' failed: "
                    + $"queued slot {conflict.QueuedSlotId} shows '{conflict.OccupyingBuildingName}' and complete "
                    + $"live dorf2 confirms no free ordinary slot for {conflict.BuildingName}. No construction "
                    + "click was attempted; the task was moved to History.");
                port.Log(
                    $"{logPrefix} ABANDONED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + "all ordinary building slots are occupied; moved to History so later queue tasks can continue");
                return true;
            }

            port.MarkDeferred(item.Id, TimeSpan.FromMinutes(5));
            port.Log(
                $"ALARM: construction task '{item.TaskName}' in village '{villageName}' could not continue: "
                + $"queued slot {conflict.QueuedSlotId} shows '{conflict.OccupyingBuildingName}', and no safe free "
                + $"ordinary slot was confirmed for {conflict.BuildingName}. Unknown ordinary slots: "
                + $"{unknownSlotText}; no construction click was attempted. The task was deferred for a fresh scan.");
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"slot {conflict.QueuedSlotId} now contains {conflict.OccupyingBuildingName}, "
                + $"but complete live dorf2 has no safe free ordinary slot for {conflict.BuildingName}; "
                + "queue item kept for a later scan");
            return true;
        }

        if (!port.MarkDeferred(item.Id, TimeSpan.Zero))
        {
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "slot conflict was confirmed, but the running item could not be returned to pending; "
                + "no construct click was attempted");
            return true;
        }

        if (!port.ApplyPendingQueueReconciliation(conflict.Updates))
        {
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"slot conflict was confirmed, but the queue changed before slot {reboundSlotId} "
                + "could be reserved; item kept for a fresh retry");
            return true;
        }

        item.Payload = conflict.Updates.Single(update => update.QueueItemId == item.Id).Payload;
        port.RequestQueueUiRefresh();
        port.Log(
            $"[building-reconcile] slot conflict: queued {conflict.BuildingName} in slot "
            + $"{conflict.QueuedSlotId}, but live dorf2 shows {conflict.OccupyingBuildingName}; "
            + $"rebound the construction chain to free slot {reboundSlotId}. It will continue on the next pass.");
        return true;
    }

    private static string? NormalizeVillageName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
