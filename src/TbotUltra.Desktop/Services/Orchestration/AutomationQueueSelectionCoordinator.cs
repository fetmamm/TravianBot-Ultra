using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutomationQueueSelectionPort
{
    BotOptions LoadOptions();
    void RemoveDisabledUtilityItems(BotOptions options);
    IReadOnlyList<QueueItem> GetQueueItems();
    string? GetVillageKey(QueueItem item);
    string? GetVillageName(QueueItem item);
    bool IsAllowedByAutomationSettings(QueueItem item);
    bool IsUtilityEnabled(string taskName, BotOptions options);
    IReadOnlyList<QueueGroup> GetConsideredGroups();
    VillageBatchSnapshot SnapshotVillageBatch();
    string? ActiveVillageKey { get; }
    string? ActiveVillageName { get; }
    QueueItem? SelectReadyConstruction(
        IReadOnlyList<QueueItem> villageItems,
        DateTimeOffset now,
        bool preview);
    void CompleteUrgentPreemption();
    void RecordUrgentPreemption(string? targetVillageKey);
    string FormatServerTime(DateTimeOffset value);
    void Log(string message);
    void LogVerbose(string message, string key);
}

internal sealed class AutomationQueueSelectionCoordinator(IAutomationQueueSelectionPort port)
{
    internal IReadOnlyList<QueueItem> GetEligibleItems()
    {
        var options = port.LoadOptions();
        return port.GetQueueItems()
            .Where(port.IsAllowedByAutomationSettings)
            .ToList();
    }

    internal QueueItem? Select(
        bool preview = false,
        DateTimeOffset? evaluationTimeUtc = null,
        string? villageKeyFilter = null,
        IReadOnlyList<QueueItem>? queueItemsOverride = null)
    {
        var options = port.LoadOptions();
        if (!preview)
        {
            port.RemoveDisabledUtilityItems(options);
        }

        var queueItems = queueItemsOverride ?? port.GetQueueItems();
        var now = evaluationTimeUtc ?? DateTimeOffset.UtcNow;
        var result = Evaluate(options, queueItems, now, villageKeyFilter, preview, activeVillageKey: null);
        if (!preview)
        {
            ApplySelectionResult(result, port.SnapshotVillageBatch(), options);
        }

        return result.Selected;
    }

    internal QueueItem? SelectUrgent(
        BotOptions options,
        IReadOnlySet<Guid> attemptedItemIds,
        bool explicitPriorityOnly)
    {
        var queueItems = port.GetQueueItems()
            .Where(item => !attemptedItemIds.Contains(item.Id))
            .Where(item => !explicitPriorityOnly || item.Priority > 0)
            .ToList();
        var result = Evaluate(
            options,
            queueItems,
            DateTimeOffset.UtcNow,
            villageKeyFilter: null,
            preview: true,
            activeVillageKey: null);
        return result.Reason == AutomationQueueSelectionReason.UrgentPreemption
            ? result.Selected
            : null;
    }

    internal AutomationQueueSelectionResult PreviewVillageHold(
        BotOptions options,
        IReadOnlySet<Guid> attemptedItemIds,
        string villageKey)
    {
        var queueItems = port.GetQueueItems()
            .Where(item => !attemptedItemIds.Contains(item.Id))
            .ToList();
        return Evaluate(
            options,
            queueItems,
            DateTimeOffset.UtcNow,
            villageKeyFilter: null,
            preview: true,
            activeVillageKey: villageKey);
    }

    internal QueueItem? SelectVillageStatusItem(
        string villageKey,
        IReadOnlySet<Guid> attemptedItemIds)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = port.GetQueueItems()
            .Select(item => new ContinuousLoopSelectionCandidate(
                item,
                port.GetVillageKey(item),
                port.IsAllowedByAutomationSettings(item),
                IsUtilityEnabled: false))
            .Where(candidate => ContinuousLoopSelector.IsVillageStatusSweepCandidate(candidate, villageKey))
            .ToList();
        var plan = ContinuousLoopSelector.CreatePlan(new ContinuousLoopSelectionInput(
            candidates,
            port.GetConsideredGroups()));
        var villageKeys = candidates.ToDictionary(candidate => candidate.Item.Id, candidate => candidate.VillageKey);

        foreach (var group in plan.OrderedGroups)
        {
            var villageItems = ContinuousLoopSelector.SelectVillageItems(
                plan.OrderedItemsByGroup[group],
                villageKeys,
                villageKey);
            if (villageItems.Count == 0)
            {
                continue;
            }

            var candidate = group == QueueGroup.Construction
                ? port.SelectReadyConstruction(villageItems, now, preview: false)
                : ContinuousLoopSelector.SelectReadyGroupHead(villageItems, now);
            if (candidate is not null && !attemptedItemIds.Contains(candidate.Id))
            {
                return candidate;
            }
        }

        return null;
    }

    private AutomationQueueSelectionResult Evaluate(
        BotOptions options,
        IReadOnlyList<QueueItem> queueItems,
        DateTimeOffset now,
        string? villageKeyFilter,
        bool preview,
        string? activeVillageKey)
    {
        var selectionCandidates = queueItems
            .Select(item => new ContinuousLoopSelectionCandidate(
                item,
                port.GetVillageKey(item),
                port.IsAllowedByAutomationSettings(item),
                ContinuousLoopSelector.IsUtilityTask(item.TaskName)
                    && port.IsUtilityEnabled(item.TaskName, options)))
            .Where(candidate => string.IsNullOrWhiteSpace(villageKeyFilter)
                || string.Equals(
                    candidate.VillageKey,
                    villageKeyFilter,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        return AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput(
                selectionCandidates,
                port.GetConsideredGroups(),
                port.SnapshotVillageBatch(),
                activeVillageKey ?? port.ActiveVillageKey,
                now,
                options.ShortVillageDeferSeconds,
                preview),
            port.SelectReadyConstruction);
    }

    private void ApplySelectionResult(
        AutomationQueueSelectionResult result,
        VillageBatchSnapshot batch,
        BotOptions options)
    {
        if (result.CompleteUrgentPreemption)
        {
            port.CompleteUrgentPreemption();
            port.Log(
                $"[village-batch] interrupted village key='{batch.VillageKey ?? "-"}' "
                + "has no ready work; urgent preemption completed.");
        }
        else if (result.Reason == AutomationQueueSelectionReason.UrgentPreemption
            && result.Selected is not null
            && !string.Equals(
                port.GetVillageKey(result.Selected),
                port.ActiveVillageKey,
                StringComparison.OrdinalIgnoreCase))
        {
            port.RecordUrgentPreemption(port.GetVillageKey(result.Selected));
            port.Log(
                $"[village-batch] urgent preemption task='{result.Selected.TaskName}' "
                + $"priority={result.Selected.Priority} village='{port.GetVillageName(result.Selected) ?? "-"}'.");
        }
        else if (result.Reason == AutomationQueueSelectionReason.UrgentResume)
        {
            port.Log(
                "[village-batch] urgent work complete; resuming "
                + $"'{port.GetVillageName(result.Selected!) ?? "-"}' "
                + $"with task='{result.Selected!.TaskName}'.");
        }
        else if (result.Reason == AutomationQueueSelectionReason.VillageRotationNoReadyWork)
        {
            port.Log(
                $"[village-batch] complete '{port.ActiveVillageName ?? "-"}' because it has no ready work; "
                + $"next='{port.GetVillageName(result.Selected!) ?? "-"}' "
                + $"task='{result.Selected!.TaskName}'.");
        }
        else if (result.Reason == AutomationQueueSelectionReason.ShortVillageHold)
        {
            port.LogVerbose(
                "[loop-pick:verbose] holding current village for short defer until "
                + $"'{port.FormatServerTime(result.HoldUntil!.Value)}' "
                + $"(limit={options.ShortVillageDeferSeconds}s)",
                $"short-village-hold:{port.ActiveVillageKey}:{result.HoldUntil.Value.UtcTicks}:{options.ShortVillageDeferSeconds}");
        }
        else if (result.Reason == AutomationQueueSelectionReason.NoEnabledGroups)
        {
            port.LogVerbose(
                "[loop-pick:verbose] no enabled groups — nothing to schedule",
                "no-enabled-groups");
        }
        else if (result.Reason == AutomationQueueSelectionReason.NoReadyWork)
        {
            port.LogVerbose(
                $"[loop-pick:verbose] no ready item selected from {result.ConsideredGroupCount} group(s)",
                $"no-selected:{result.ConsideredGroupCount}");
        }
    }
}
