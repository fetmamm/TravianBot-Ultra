using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Stateless selection and timing rules used by the continuous loop.
/// </summary>
internal static class ContinuousLoopSelector
{
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
}
