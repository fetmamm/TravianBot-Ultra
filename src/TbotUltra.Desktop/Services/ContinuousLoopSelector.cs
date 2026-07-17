using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

internal sealed record ContinuousLoopSelectionCandidate(
    QueueItem Item,
    string? VillageKey,
    bool IsAllowedByAutomationSettings,
    bool IsUtilityEnabled);

internal sealed record ContinuousLoopSelectionInput(
    IReadOnlyList<ContinuousLoopSelectionCandidate> Candidates,
    IReadOnlyList<QueueGroup> ConfiguredGroups);

internal sealed record ContinuousLoopUtilitySelectionInput(
    IReadOnlyList<ContinuousLoopSelectionCandidate> Candidates,
    string? ActiveVillageKey,
    DateTimeOffset Now);

internal sealed record ContinuousLoopUtilitySelectionResult(
    IReadOnlyList<QueueItem> ReadyItems,
    QueueItem? PreferredItem);

internal sealed record ContinuousLoopSelectionPlan(
    IReadOnlyList<QueueGroup> OrderedGroups,
    IReadOnlyDictionary<QueueGroup, IReadOnlyList<QueueItem>> OrderedItemsByGroup);

internal sealed record ContinuousLoopGroupSelectionInput(
    QueueGroup Group,
    IReadOnlyList<QueueItem> OrderedItems,
    string? RotationVillageKey,
    DateTimeOffset Now,
    IReadOnlyDictionary<Guid, string?> VillageKeys);

internal sealed record ContinuousLoopGroupSelectionResult(
    QueueItem? Item,
    string? RotationVillageKey);

/// <summary>
/// Stateless selection and timing rules used by the continuous loop.
/// </summary>
internal static class ContinuousLoopSelector
{
    internal static readonly TimeSpan ShortVillageDeferThreshold = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan KeepAliveImminentWorkThreshold = TimeSpan.FromSeconds(60);

    internal static ContinuousLoopUtilitySelectionResult SelectUtility(
        ContinuousLoopUtilitySelectionInput input)
    {
        var readyUtilityCandidates = OrderItems(input.Candidates
            .Where(candidate =>
                IsUtilityTask(candidate.Item.TaskName)
                && candidate.IsUtilityEnabled
                && candidate.IsAllowedByAutomationSettings
                && candidate.Item.Status == QueueStatus.Pending
                && candidate.Item.NextAttemptAt <= input.Now));
        var readyUtilityItems = readyUtilityCandidates
            .Select(candidate => candidate.Item)
            .ToList();
        var preferredUtilityItem = readyUtilityCandidates
            .FirstOrDefault(candidate =>
                input.ActiveVillageKey is null
                || string.Equals(candidate.VillageKey, input.ActiveVillageKey, StringComparison.OrdinalIgnoreCase))
            ?.Item;

        return new ContinuousLoopUtilitySelectionResult(readyUtilityItems, preferredUtilityItem);
    }

    internal static ContinuousLoopSelectionPlan CreatePlan(ContinuousLoopSelectionInput input)
    {
        var queueItems = input.Candidates.Select(candidate => candidate.Item).ToList();
        var orderedGroups = BuildConsideredGroups(input.ConfiguredGroups, queueItems);
        var orderedItemsByGroup = orderedGroups.ToDictionary(
            group => group,
            group => (IReadOnlyList<QueueItem>)OrderItems(input.Candidates
                .Where(candidate =>
                    candidate.Item.Group == group
                    && !IsUtilityTask(candidate.Item.TaskName)
                    && candidate.IsAllowedByAutomationSettings
                    && candidate.Item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused))
                .Select(candidate => candidate.Item)
                .ToList());

        return new ContinuousLoopSelectionPlan(orderedGroups, orderedItemsByGroup);
    }

    internal static ContinuousLoopGroupSelectionResult SelectNonConstructionGroup(
        ContinuousLoopGroupSelectionInput input)
    {
        var rotationVillageKey = input.RotationVillageKey;
        var candidate = QueueVillageRotation.SelectByVillageRotation(
            input.OrderedItems,
            item => input.VillageKeys.TryGetValue(item.Id, out var villageKey) ? villageKey : null,
            villageItems => input.Group == QueueGroup.Hero
                ? SelectReadyHeroGroupItem(villageItems, input.Now)
                : SelectReadyGroupHead(villageItems, input.Now),
            ref rotationVillageKey);

        return new ContinuousLoopGroupSelectionResult(candidate, rotationVillageKey);
    }

    internal static bool IsUtilityTask(string? taskName) =>
        string.Equals(taskName, "collect_tasks", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "collect_daily_quests", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<QueueGroup> BuildConsideredGroups(
        IEnumerable<QueueGroup> configuredGroups,
        IEnumerable<QueueItem> queueItems)
    {
        var groups = configuredGroups.ToList();
        var seen = groups.ToHashSet();
        foreach (var group in queueItems
            .Where(item => !item.IsRuntimeOnly
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .Select(item => item.Group))
        {
            if (seen.Add(group))
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    internal static QueueItem? SelectReadyGroupHead(
        IReadOnlyList<QueueItem> villageItems,
        DateTimeOffset now)
    {
        var head = villageItems.FirstOrDefault();
        return head is not null
            && head.Status == QueueStatus.Pending
            && head.NextAttemptAt <= now
                ? head
                : null;
    }

    internal static QueueItem? SelectReadyHeroGroupItem(
        IReadOnlyList<QueueItem> villageItems,
        DateTimeOffset now)
    {
        return SelectReadyGroupHead(villageItems, now)
            ?? villageItems.FirstOrDefault(item =>
                string.Equals(item.TaskName, "spend_hero_attribute_points", StringComparison.OrdinalIgnoreCase)
                && item.Status == QueueStatus.Pending
                && item.NextAttemptAt <= now);
    }

    internal static IReadOnlyList<QueueItem> SelectVillageItems(
        IReadOnlyList<QueueItem> orderedItems,
        IReadOnlyDictionary<Guid, string?> villageKeys,
        string villageKey,
        bool includeVillageLess = false)
    {
        return orderedItems
            .Where(item => villageKeys.TryGetValue(item.Id, out var itemVillageKey)
                && (string.Equals(itemVillageKey, villageKey, StringComparison.OrdinalIgnoreCase)
                    || (includeVillageLess && string.IsNullOrWhiteSpace(itemVillageKey))))
            .ToList();
    }

    internal static DateTimeOffset? ResolveShortVillageHoldUntil(
        IEnumerable<ContinuousLoopSelectionCandidate> candidates,
        string? activeVillageKey,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(activeVillageKey))
        {
            return null;
        }

        var holdUntil = candidates
            .Where(candidate =>
                candidate.IsAllowedByAutomationSettings
                && !IsUtilityTask(candidate.Item.TaskName)
                && candidate.Item.Status == QueueStatus.Pending
                && candidate.Item.NextAttemptAt > now
                && candidate.Item.NextAttemptAt <= now.Add(ShortVillageDeferThreshold)
                && string.Equals(candidate.VillageKey, activeVillageKey, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => (DateTimeOffset?)candidate.Item.NextAttemptAt)
            .Min();

        return holdUntil;
    }

    internal static bool ShouldDeferKeepAliveForImminentWork(
        DateTimeOffset now,
        DateTimeOffset? nextPendingAt)
    {
        return nextPendingAt is DateTimeOffset next
            && next > now
            && next <= now.Add(KeepAliveImminentWorkThreshold);
    }

    internal static TimeSpan ResolveReinforcementSendDelay(
        BotOptions options,
        IReadOnlyList<QueueItem> queueItems,
        DateTimeOffset now)
    {
        var lastSucceeded = queueItems
            .Where(item => string.Equals(item.TaskName, "send_reinforcements_between_villages", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Status == QueueStatus.Succeeded)
            .Select(item => (DateTimeOffset?)item.UpdatedAt)
            .Max();
        if (lastSucceeded is null)
        {
            return TimeSpan.Zero;
        }

        var minMinutes = ReinforcementSendDefaults.NormalizeSendMinMinutes(options.ReinforcementsSendMinMinutes);
        var maxMinutes = ReinforcementSendDefaults.NormalizeSendMaxMinutes(options.ReinforcementsSendMaxMinutes);
        var nextSendAt = lastSucceeded.Value.Add(ReinforcementSendDefaults.CalculateSendDelay(minMinutes, maxMinutes));
        var remaining = nextSendAt - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal static bool PayloadEquals(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> updated)
    {
        if (current.Count != updated.Count)
        {
            return false;
        }

        foreach (var pair in updated)
        {
            if (!current.TryGetValue(pair.Key, out var existingValue)
                || !string.Equals(existingValue, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<ContinuousLoopSelectionCandidate> OrderItems(
        IEnumerable<ContinuousLoopSelectionCandidate> candidates)
    {
        return candidates
            .OrderBy(candidate => candidate.Item.IsRuntimeOnly)
            .ThenByDescending(candidate => candidate.Item.Priority)
            .ThenBy(candidate => candidate.Item.CreatedAt)
            .ToList();
    }
}
