using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutomationMissingBuildingUpgradeRecoveryPort
{
    string? GetVillageName(QueueItem item);
    ValueTask<VillageStatus> ReadLiveVillageStatusAsync(
        BotOptions options,
        QueueItem item,
        CancellationToken cancellationToken);
    void ApplyLiveVillageStatus(VillageStatus status, string? villageName);
    void ReconcileLiveQueue(VillageStatus status);
    bool MarkDeferred(Guid itemId, TimeSpan delay);
    bool MarkSucceeded(Guid itemId);
    bool RemoveQueueItem(Guid itemId);
    bool UpdatePendingQueueItem(
        Guid itemId,
        Dictionary<string, string> payload,
        int? priority,
        TimeSpan delay);
    IReadOnlyList<QueueItem> GetQueueItems();
    bool IsSameVillageOrGlobal(QueueItem source, QueueItem candidate);
    QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries);
    void RequestQueueUiRefresh(Guid? selectedItemId = null);
    ValueTask RefreshVillageActivityIndicatorsAsync();
    void Log(string message);
}

internal sealed class AutomationMissingBuildingUpgradeRecovery(
    IAutomationMissingBuildingUpgradeRecoveryPort port)
{
    internal async ValueTask<bool> TryRecoverAsync(
        QueueItem item,
        BotOptions options,
        BotTaskExecutionResult executionResult,
        string logPrefix,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        if (executionResult.LastTask?.ConstructionOutcome != ConstructionTaskOutcome.MissingBuilding
            || !BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
            || upgrade is null)
        {
            return false;
        }

        var gid = BuildingCatalogService.GidForName(upgrade.Name);
        if (gid is null)
        {
            port.Log(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"slot {upgrade.SlotId} is empty, but building '{upgrade.Name ?? "-"}' has no catalog gid; "
                + "upgrade kept for retry");
            port.MarkDeferred(item.Id, TimeSpan.FromMinutes(1));
            return true;
        }

        VillageStatus liveStatus;
        var targetVillageName = port.GetVillageName(item);
        try
        {
            port.Log(
                $"[building-repair] validating the complete live dorf2 overview before reconstructing "
                + $"{upgrade.Name} for empty slot {upgrade.SlotId}.");
            liveStatus = await port.ReadLiveVillageStatusAsync(options, item, cancellationToken);
            port.ApplyLiveVillageStatus(liveStatus, targetVillageName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"slot {upgrade.SlotId} looked empty, but full live dorf2 validation failed: "
                + $"{exception.Message}; no reconstruction was queued");
            port.MarkDeferred(item.Id, TimeSpan.FromMinutes(1));
            return true;
        }

        if (BuildingUpgradeSlotRebindPlanner.PlanUpgradeFromLiveStatus(liveStatus, item) is { } reconciliation)
        {
            if (reconciliation.TargetSatisfied)
            {
                port.MarkSucceeded(item.Id);
                port.RemoveQueueItem(item.Id);
                port.ReconcileLiveQueue(liveStatus);
                port.RequestQueueUiRefresh();
                port.Log(
                    $"{logPrefix} RECOVERED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"fresh dorf2 confirms {reconciliation.BuildingName} at slot {reconciliation.LiveSlotId} "
                    + $"level {reconciliation.LiveLevel}, meeting target {reconciliation.TargetLevel}; "
                    + "removed stale upgrade without reconstruction");
                return true;
            }

            if (!port.MarkDeferred(item.Id, TimeSpan.Zero)
                || !port.UpdatePendingQueueItem(item.Id, reconciliation.Payload, null, TimeSpan.Zero))
            {
                port.Log(
                    $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"fresh dorf2 found {reconciliation.BuildingName} at slot {reconciliation.LiveSlotId}, "
                    + "but the queued slot could not be updated; no reconstruction was queued");
                return true;
            }

            item.Payload = reconciliation.Payload;
            port.ReconcileLiveQueue(liveStatus);
            port.RequestQueueUiRefresh();
            port.Log(
                $"{logPrefix} RECOVERED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"fresh dorf2 found {reconciliation.BuildingName} level {reconciliation.LiveLevel}; "
                + $"rebound upgrade from slot {reconciliation.QueuedSlotId} to {reconciliation.LiveSlotId} "
                + "without reconstruction");
            return true;
        }

        var overviewComplete = BuildingUpgradeSlotRebindPlanner.HasCompleteBuildingOverview(liveStatus);
        var hasIdentityEvidence = BuildingUpgradeSlotRebindPlanner.HasLiveBuildingIdentity(liveStatus, gid.Value);
        if (!overviewComplete || hasIdentityEvidence)
        {
            port.MarkDeferred(item.Id, TimeSpan.FromMinutes(1));
            var reason = !overviewComplete
                ? $"dorf2 returned fewer than 22 distinct building slots ({liveStatus.Buildings.Count} rows)"
                : $"dorf2 still contains gid/name identity evidence for {upgrade.Name}";
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"slot {upgrade.SlotId} looked empty, but {reason}; no reconstruction was queued");
            return true;
        }

        var constructPayload = BuildReconstructionPayload(item, upgrade, gid.Value);
        var queueItems = port.GetQueueItems();
        var existingConstruct = queueItems.FirstOrDefault(candidate =>
            candidate.Id != item.Id
            && candidate.Status == QueueStatus.Pending
            && port.IsSameVillageOrGlobal(item, candidate)
            && BuildingConstructPayload.TryFromDictionary(candidate.Payload, out var construct)
            && construct is not null
            && construct.SlotId == upgrade.SlotId
            && construct.Gid == gid.Value);
        var maxPriority = queueItems.Select(candidate => candidate.Priority).DefaultIfEmpty(item.Priority).Max();
        var repairPriority = maxPriority == int.MaxValue ? int.MaxValue : maxPriority + 1;
        Guid repairId;

        if (existingConstruct is not null)
        {
            if (!port.UpdatePendingQueueItem(
                    existingConstruct.Id,
                    constructPayload,
                    repairPriority,
                    TimeSpan.Zero))
            {
                port.Log(
                    $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + $"could not promote queued reconstruction for slot {upgrade.SlotId}; upgrade kept for retry");
                port.MarkDeferred(item.Id, TimeSpan.FromMinutes(1));
                return true;
            }

            repairId = existingConstruct.Id;
        }
        else
        {
            repairId = port.Enqueue("construct_building", constructPayload, repairPriority, maxRetries: 3).Id;
        }

        if (!port.MarkDeferred(item.Id, TimeSpan.FromSeconds(30)))
        {
            port.Log(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"queued reconstruction id={repairId}, but could not keep the parent upgrade pending");
            return false;
        }

        port.RequestQueueUiRefresh(repairId);
        await port.RefreshVillageActivityIndicatorsAsync();
        port.Log(
            $"{logPrefix} REPAIR {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
            + $"slot {upgrade.SlotId} is confirmed empty; queued {upgrade.Name} reconstruction in the same slot "
            + $"(id={repairId}, priority={repairPriority}) and kept target upgrade queued.");
        return true;
    }

    private static Dictionary<string, string> BuildReconstructionPayload(
        QueueItem item,
        BuildingUpgradePayload upgrade,
        int gid)
    {
        var payload = new BuildingConstructPayload(upgrade.SlotId, gid, upgrade.Name).ToDictionary();
        payload[BotOptionPayloadKeys.BuildingConstructAllowSlotFallback] = bool.FalseString;
        payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByConstructionRequirementRepair;
        payload[BotOptionPayloadKeys.AutoAddedParentId] = item.Id.ToString();
        payload[BotOptionPayloadKeys.AutoAddedReason] =
            $"Reconstruct canceled {upgrade.Name} in empty slot {upgrade.SlotId}";
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.TargetVillageName);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.TargetVillageUrl);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.TargetVillageKey);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.NpcTradeEnabled);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.ConstructFasterEnabled);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.ConstructFasterMinBuildMinutes);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.ConstructFasterRandomEnabled);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.ConstructFasterRandomChancePercent);
        CopyIfPresent(item.Payload, payload, BotOptionPayloadKeys.BuildingTemplateStepId);
        return payload;
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }
}
