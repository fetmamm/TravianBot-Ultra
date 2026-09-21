using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal enum AutomationQueueSelectionReason
{
    Selected,
    UrgentPreemption,
    UrgentResume,
    VillageRotationNoReadyWork,
    ShortVillageHold,
    NoEnabledGroups,
    NoReadyWork,
}

internal sealed record AutomationQueueSelectionInput(
    IReadOnlyList<ContinuousLoopSelectionCandidate> Candidates,
    IReadOnlyList<QueueGroup> ConfiguredGroups,
    VillageBatchSnapshot VillageBatch,
    string? ActiveVillageKey,
    DateTimeOffset Now,
    int ShortVillageDeferSeconds,
    bool Preview);

internal sealed record AutomationQueueSelectionResult(
    QueueItem? Selected,
    AutomationQueueSelectionReason Reason,
    int ConsideredGroupCount,
    DateTimeOffset? HoldUntil = null,
    bool CompleteUrgentPreemption = false);

internal static class AutomationQueueSelector
{
    internal static AutomationQueueSelectionResult Select(
        AutomationQueueSelectionInput input,
        Func<IReadOnlyList<QueueItem>, DateTimeOffset, bool, QueueItem?> selectReadyConstruction)
    {
        var villageKeysByItemId = input.Candidates
            .ToDictionary(candidate => candidate.Item.Id, candidate => candidate.VillageKey);
        var utilitySelection = ContinuousLoopSelector.SelectUtility(
            new ContinuousLoopUtilitySelectionInput(input.Candidates, input.ActiveVillageKey, input.Now));
        var urgentUtilityItem = utilitySelection.ReadyItems.FirstOrDefault(ContinuousLoopSelector.IsUrgentItem);
        if (urgentUtilityItem is not null)
        {
            return new AutomationQueueSelectionResult(
                urgentUtilityItem,
                AutomationQueueSelectionReason.UrgentPreemption,
                0);
        }

        var selectionPlan = ContinuousLoopSelector.CreatePlan(new ContinuousLoopSelectionInput(
            input.Candidates,
            input.ConfiguredGroups));
        if (selectionPlan.OrderedGroups.Count == 0)
        {
            return utilitySelection.PreferredItem is not null
                ? new AutomationQueueSelectionResult(
                    utilitySelection.PreferredItem,
                    AutomationQueueSelectionReason.Selected,
                    0,
                    CompleteUrgentPreemption: input.VillageBatch.HasUrgentPreemption)
                : new AutomationQueueSelectionResult(
                    null,
                    AutomationQueueSelectionReason.NoEnabledGroups,
                    0,
                    CompleteUrgentPreemption: input.VillageBatch.HasUrgentPreemption);
        }

        var urgentCandidate = SelectReadyItemAcrossVillages(
            selectionPlan,
            villageKeysByItemId,
            input.Now,
            preview: true,
            excludedVillageKey: null,
            urgentOnly: true,
            commitConstruction: !input.Preview,
            selectReadyConstruction);
        if (urgentCandidate is not null)
        {
            return new AutomationQueueSelectionResult(
                urgentCandidate,
                AutomationQueueSelectionReason.UrgentPreemption,
                selectionPlan.OrderedGroups.Count);
        }

        var currentVillageCandidate = string.IsNullOrWhiteSpace(input.VillageBatch.VillageKey)
            ? null
            : SelectReadyItemForVillage(
                selectionPlan,
                villageKeysByItemId,
                input.VillageBatch.VillageKey,
                input.Now,
                input.Preview,
                selectReadyConstruction);
        var otherVillageCandidate = SelectReadyItemAcrossVillages(
            selectionPlan,
            villageKeysByItemId,
            input.Now,
            preview: true,
            excludedVillageKey: input.VillageBatch.VillageKey,
            urgentOnly: false,
            commitConstruction: !input.Preview,
            selectReadyConstruction);

        if (currentVillageCandidate is not null)
        {
            return new AutomationQueueSelectionResult(
                currentVillageCandidate,
                input.VillageBatch.HasUrgentPreemption
                    && !string.Equals(
                        input.VillageBatch.VillageKey,
                        input.ActiveVillageKey,
                        StringComparison.OrdinalIgnoreCase)
                    ? AutomationQueueSelectionReason.UrgentResume
                    : AutomationQueueSelectionReason.Selected,
                selectionPlan.OrderedGroups.Count);
        }

        if (otherVillageCandidate is not null)
        {
            return new AutomationQueueSelectionResult(
                otherVillageCandidate,
                AutomationQueueSelectionReason.VillageRotationNoReadyWork,
                selectionPlan.OrderedGroups.Count,
                CompleteUrgentPreemption: input.VillageBatch.HasUrgentPreemption);
        }

        var readyUtilityItem = utilitySelection.ReadyItems.FirstOrDefault();
        if (readyUtilityItem is not null)
        {
            return new AutomationQueueSelectionResult(
                readyUtilityItem,
                AutomationQueueSelectionReason.Selected,
                selectionPlan.OrderedGroups.Count,
                CompleteUrgentPreemption: input.VillageBatch.HasUrgentPreemption);
        }

        var holdUntil = ContinuousLoopSelector.ResolveShortVillageHoldUntil(
            input.Candidates,
            input.ActiveVillageKey,
            input.Now,
            input.ShortVillageDeferSeconds);
        return holdUntil is not null
            ? new AutomationQueueSelectionResult(
                null,
                AutomationQueueSelectionReason.ShortVillageHold,
                selectionPlan.OrderedGroups.Count,
                holdUntil,
                CompleteUrgentPreemption: input.VillageBatch.HasUrgentPreemption)
            : new AutomationQueueSelectionResult(
                null,
                AutomationQueueSelectionReason.NoReadyWork,
                selectionPlan.OrderedGroups.Count,
                CompleteUrgentPreemption: input.VillageBatch.HasUrgentPreemption);
    }

    private static QueueItem? SelectReadyItemForVillage(
        ContinuousLoopSelectionPlan selectionPlan,
        IReadOnlyDictionary<Guid, string?> villageKeysByItemId,
        string villageKey,
        DateTimeOffset now,
        bool preview,
        Func<IReadOnlyList<QueueItem>, DateTimeOffset, bool, QueueItem?> selectReadyConstruction)
    {
        foreach (var group in selectionPlan.OrderedGroups)
        {
            var villageItems = ContinuousLoopSelector.SelectVillageItems(
                selectionPlan.OrderedItemsByGroup[group],
                villageKeysByItemId,
                villageKey,
                includeVillageLess: group == QueueGroup.Hero);
            var candidate = SelectReadyItemWithinGroup(
                group,
                villageItems,
                now,
                preview,
                selectReadyConstruction);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static QueueItem? SelectReadyItemAcrossVillages(
        ContinuousLoopSelectionPlan selectionPlan,
        IReadOnlyDictionary<Guid, string?> villageKeysByItemId,
        DateTimeOffset now,
        bool preview,
        string? excludedVillageKey,
        bool urgentOnly,
        bool commitConstruction,
        Func<IReadOnlyList<QueueItem>, DateTimeOffset, bool, QueueItem?> selectReadyConstruction)
    {
        foreach (var group in selectionPlan.OrderedGroups)
        {
            var groupItems = selectionPlan.OrderedItemsByGroup[group]
                .Where(item => !urgentOnly || ContinuousLoopSelector.IsUrgentItem(item))
                .Where(item => string.IsNullOrWhiteSpace(excludedVillageKey)
                    || (villageKeysByItemId.TryGetValue(item.Id, out var villageKey)
                        && !string.IsNullOrWhiteSpace(villageKey)
                        && !string.Equals(villageKey, excludedVillageKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (groupItems.Count == 0)
            {
                continue;
            }

            string? rotationVillageKey = null;
            var candidate = QueueVillageRotation.SelectByVillageRotation(
                groupItems,
                item => villageKeysByItemId.TryGetValue(item.Id, out var villageKey) ? villageKey : null,
                villageItems =>
                {
                    var ready = SelectReadyItemWithinGroup(
                        group,
                        villageItems,
                        now,
                        preview,
                        selectReadyConstruction);
                    // Commit even a blocked remote construction: a full queue can persist its
                    // finish-plus-humanization deadline without visiting that village.
                    return group == QueueGroup.Construction && commitConstruction
                        ? SelectReadyItemWithinGroup(group, villageItems, now, preview: false, selectReadyConstruction)
                        : ready;
                },
                ref rotationVillageKey);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static QueueItem? SelectReadyItemWithinGroup(
        QueueGroup group,
        IReadOnlyList<QueueItem> villageItems,
        DateTimeOffset now,
        bool preview,
        Func<IReadOnlyList<QueueItem>, DateTimeOffset, bool, QueueItem?> selectReadyConstruction)
    {
        if (villageItems.Count == 0)
        {
            return null;
        }

        if (group == QueueGroup.Construction)
        {
            return selectReadyConstruction(villageItems, now, preview);
        }

        return group == QueueGroup.Hero
            ? ContinuousLoopSelector.SelectReadyHeroGroupItem(villageItems, now)
            : ContinuousLoopSelector.SelectReadyGroupHead(villageItems, now);
    }
}
