using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed record AutomationConstructionRequirementContext(
    VillageStatus? Status,
    IReadOnlyList<QueueItem> SameVillageItems);

internal interface IAutomationConstructionRequirementGuardPort
{
    AutomationConstructionRequirementContext GetContext(QueueItem item);
    bool MarkDeferred(Guid itemId, TimeSpan delay);
    bool PatchDeferred(
        Guid itemId,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string> keysToRemove);
    bool MarkPermanentlyFailed(Guid itemId);
    IReadOnlyList<QueueItem> GetQueueItems();
    bool UpdatePendingQueueItem(
        Guid itemId,
        Dictionary<string, string> payload,
        int priority,
        TimeSpan delay);
    QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries);
    void RequestQueueUiRefresh(Guid? selectedItemId = null);
    ValueTask RefreshVillageActivityIndicatorsAsync();
    void RaisePermanentFailureAlarm(QueueItem item, string message);
    void Log(string message);
}

internal sealed class AutomationConstructionRequirementGuard(
    IAutomationConstructionRequirementGuardPort port,
    TimeProvider? timeProvider = null)
{
    private static readonly string[] TransientPayloadKeys =
    [
        BotOptionPayloadKeys.RequirementDeferCount,
        BotOptionPayloadKeys.ConstructionPreSleepFill,
        BotOptionPayloadKeys.ConstructionLoginFill,
        BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds,
        BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied,
        BotOptionPayloadKeys.QueueHumanizeExtraSeconds,
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal bool TryHandleUpgradeWaitingForConstruct(
        QueueItem item,
        string logPrefix,
        Stopwatch timer)
    {
        var dependency = ConstructionDependencyGate.ResolveUpgradeWaitingForConstruct(
            item,
            port.GetContext(item).SameVillageItems,
            _timeProvider.GetUtcNow());
        if (dependency is null)
        {
            return false;
        }

        if (!port.MarkDeferred(item.Id, dependency.Delay))
        {
            port.Log(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"could not defer upgrade waiting for {dependency.Detail}");
            return false;
        }

        port.RequestQueueUiRefresh();
        port.Log(
            $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
            + $"waiting for queued {dependency.Detail}; retry in {dependency.Delay.TotalSeconds:F0}s");
        return true;
    }

    internal async ValueTask<bool> TryHandleAsync(
        QueueItem item,
        string logPrefix,
        Stopwatch timer)
    {
        var context = port.GetContext(item);
        if (context.Status is null)
        {
            return false;
        }

        var result = ConstructionDependencyGate.ResolveConstructRequirementGuard(
            item,
            context.Status,
            context.SameVillageItems,
            _timeProvider.GetUtcNow());
        if (result.Action == ConstructionRequirementGuardAction.None)
        {
            return false;
        }

        if (result.Action is ConstructionRequirementGuardAction.DeferForQueuedPrerequisite
            or ConstructionRequirementGuardAction.FailMissingPrerequisite)
        {
            if (await TryRepairAsync(item, context, result, logPrefix, timer))
            {
                return true;
            }
        }

        if (result.Action is ConstructionRequirementGuardAction.DeferForActivePrerequisite
            or ConstructionRequirementGuardAction.DeferForQueuedPrerequisite)
        {
            var delay = result.Delay ?? TimeSpan.FromSeconds(60);
            var payload = BuildRequirementDeferPayload(item.Payload);
            if (!port.MarkDeferred(item.Id, delay))
            {
                port.Log(
                    $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                    + "construct prerequisite wait detected, but defer could not be persisted before worker execution");
                return false;
            }

            if (port.PatchDeferred(item.Id, RequirementDeferValues(), TransientPayloadKeys))
            {
                item.Payload = payload;
            }
            else
            {
                port.Log(
                    $"[construction-dependency] prerequisite defer payload persistence failed "
                    + $"id={item.Id} task='{item.TaskName}'");
            }

            var source = result.Action == ConstructionRequirementGuardAction.DeferForActivePrerequisite
                ? "active prerequisite"
                : "queued prerequisite";
            port.Log(
                $"{logPrefix} DEFER {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"construct requirements waiting for {source}: {result.Detail}. "
                + $"Next try in {delay.TotalSeconds:F0}s; worker was not started.");
            await port.RefreshVillageActivityIndicatorsAsync();
            return true;
        }

        if (port.MarkPermanentlyFailed(item.Id))
        {
            var message =
                $"construct requirements missing with no same-village queued or active prerequisite: {result.Detail}";
            port.Log(
                $"{logPrefix} ABANDONED {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + $"{message}. Removed from the active queue before worker execution.");
            port.RaisePermanentFailureAlarm(item, message);
            await port.RefreshVillageActivityIndicatorsAsync();
            return true;
        }

        port.Log(
            $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
            + $"construct requirements missing ({result.Detail}) but terminal failure could not be persisted");
        return false;
    }

    internal static Dictionary<string, string> BuildRepairPayload(
        QueueItem parent,
        ConstructionRequirementRepairStep step,
        bool markAsAutomaticRepair)
    {
        var payload = new Dictionary<string, string>(step.Payload, StringComparer.OrdinalIgnoreCase);
        if (markAsAutomaticRepair)
        {
            payload[BotOptionPayloadKeys.AutoAddedBy] =
                BotOptionPayloadKeys.AutoAddedByConstructionRequirementRepair;
            payload[BotOptionPayloadKeys.AutoAddedParentId] = parent.Id.ToString();
            payload[BotOptionPayloadKeys.AutoAddedReason] = step.Reason;
            payload[BotOptionPayloadKeys.AutoAddedRequirement] = step.RequirementText;
        }

        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageName);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageUrl);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.TargetVillageKey);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.NpcTradeEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterMinBuildMinutes);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterRandomEnabled);
        CopyIfPresent(parent.Payload, payload, BotOptionPayloadKeys.ConstructFasterRandomChancePercent);
        return payload;
    }

    private async ValueTask<bool> TryRepairAsync(
        QueueItem item,
        AutomationConstructionRequirementContext context,
        ConstructionRequirementGuardResult guardResult,
        string logPrefix,
        Stopwatch timer)
    {
        var plan = ConstructionRequirementRepairPlanner.Plan(
            item,
            context.Status!,
            context.SameVillageItems,
            _timeProvider.GetUtcNow());
        if (plan.HasBlockers)
        {
            port.Log($"[construction-repair] cannot repair construct requirements for id={item.Id}: {plan.Detail}");
            return false;
        }

        if (!plan.HasSteps)
        {
            return false;
        }

        var queueItems = port.GetQueueItems();
        var maxPriority = queueItems.Select(entry => entry.Priority).DefaultIfEmpty(item.Priority).Max();
        var firstPriority = maxPriority > int.MaxValue - plan.Steps.Count
            ? int.MaxValue
            : maxPriority + plan.Steps.Count;
        var changedIds = new List<Guid>();
        var created = 0;
        var promoted = 0;

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var priority = firstPriority == int.MaxValue ? int.MaxValue - index : firstPriority - index;
            var payload = BuildRepairPayload(
                item,
                step,
                step.Kind == ConstructionRequirementRepairStepKind.Enqueue);

            if (step.Kind == ConstructionRequirementRepairStepKind.Promote
                && step.ExistingQueueItemId is Guid existingId)
            {
                var existing = queueItems.FirstOrDefault(entry => entry.Id == existingId);
                if (existing?.Status != QueueStatus.Pending)
                {
                    port.Log(
                        $"[construction-repair] skipped promote id={existingId}: "
                        + $"item is {existing?.Status.ToString() ?? "missing"}.");
                    continue;
                }

                if (port.UpdatePendingQueueItem(existingId, payload, priority, TimeSpan.Zero))
                {
                    changedIds.Add(existingId);
                    promoted++;
                    port.Log(
                        $"[construction-repair] promoted queued repair id={existingId} "
                        + $"priority={priority}: {step.Reason}.");
                }
                else
                {
                    port.Log(
                        $"[construction-repair] failed to promote queued repair id={existingId}: {step.Reason}.");
                }

                continue;
            }

            var repairItem = port.Enqueue(step.TaskName, payload, priority, maxRetries: 3);
            changedIds.Add(repairItem.Id);
            created++;
            port.Log(
                $"[construction-repair] queued automatic repair id={repairItem.Id} "
                + $"priority={priority}: {step.Reason}.");
        }

        if (changedIds.Count == 0)
        {
            return false;
        }

        var parentPayload = BuildRequirementDeferPayload(item.Payload);
        var parentDelay = guardResult.Delay ?? TimeSpan.FromSeconds(60);
        if (!port.MarkDeferred(item.Id, parentDelay))
        {
            port.Log(
                $"{logPrefix} FAIL {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
                + "automatic construct requirement repair was queued, but parent defer could not be persisted");
            return false;
        }

        if (port.PatchDeferred(item.Id, RequirementDeferValues(), TransientPayloadKeys))
        {
            item.Payload = parentPayload;
        }
        else
        {
            port.Log(
                $"[construction-repair] parent defer payload persistence failed "
                + $"id={item.Id} task='{item.TaskName}'.");
        }

        port.RequestQueueUiRefresh(changedIds[0]);
        await port.RefreshVillageActivityIndicatorsAsync();
        port.Log(
            $"{logPrefix} REPAIR {timer.Elapsed.TotalSeconds:F1}s task={item.TaskName} | "
            + $"requirements '{guardResult.Detail}' missing; automatic repair queued/promoted "
            + $"created={created}, promoted={promoted}. Parent retries in {parentDelay.TotalSeconds:F0}s.");
        return true;
    }

    private static Dictionary<string, string> BuildRequirementDeferPayload(
        IReadOnlyDictionary<string, string> source)
    {
        var payload = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        foreach (var key in TransientPayloadKeys)
        {
            payload.Remove(key);
        }
        foreach (var pair in RequirementDeferValues())
        {
            payload[pair.Key] = pair.Value;
        }
        return payload;
    }

    private static Dictionary<string, string> RequirementDeferValues() => new(StringComparer.OrdinalIgnoreCase)
    {
        [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
        [BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
            ConstructionQueueState.CurrentDeferClassificationVersion,
    };

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
