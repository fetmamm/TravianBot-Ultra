using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Stateless selection and timing rules used by the continuous loop.
/// </summary>
internal static class ContinuousLoopSelector
{
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

    internal static QueueItem? SelectReadyUtilityItem(
        IReadOnlyList<QueueItem> orderedReadyItems,
        string? activeVillageKey,
        Func<QueueItem, string?> villageKeySelector)
    {
        return orderedReadyItems.FirstOrDefault(item =>
            activeVillageKey is null
            || string.Equals(villageKeySelector(item), activeVillageKey, StringComparison.OrdinalIgnoreCase));
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
}
