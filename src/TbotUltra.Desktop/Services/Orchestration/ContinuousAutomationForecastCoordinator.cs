using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IContinuousAutomationForecastPort
{
    IReadOnlyList<QueueItem> GetQueueItems();
    string? GetVillageKey(QueueItem item);
    bool IsAllowedByAutomationSettings(QueueItem item);
    TimeSpan? ResolveConstructionQueueDelay(QueueItem item, DateTimeOffset now);
    TimeSpan? ResolveSmartSleepConstructionQueueClearDelay(QueueItem item, DateTimeOffset now);
    TimeSpan? ResolveConstructPrerequisiteDelay(QueueItem item, DateTimeOffset now);
    QueueItem? SelectPreview(
        DateTimeOffset evaluationTime,
        string? villageKeyFilter,
        IReadOnlyList<QueueItem> queueItems);
    bool HasKnownConstructionAvailability(QueueItem item, DateTimeOffset now);
}

internal sealed class ContinuousAutomationForecastCoordinator(IContinuousAutomationForecastPort port)
{
    internal ContinuousLoopForecast Resolve(
        DateTimeOffset now,
        string? villageKeyFilter = null,
        IReadOnlyList<QueueItem>? queueItemsOverride = null,
        bool wakeWhenConstructionQueueClears = false)
    {
        var queueItems = queueItemsOverride ?? port.GetQueueItems();
        var scopedItems = queueItems
            .Where(item => string.IsNullOrWhiteSpace(villageKeyFilter)
                || string.Equals(
                    port.GetVillageKey(item),
                    villageKeyFilter,
                    StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Status == QueueStatus.Running
                || port.IsAllowedByAutomationSettings(item))
            .ToList();
        var pending = scopedItems
            .Where(item => item.Status == QueueStatus.Pending)
            .ToList();
        var candidateDeadlines = new HashSet<DateTimeOffset>();
        foreach (var item in pending)
        {
            var queueClearDelay = item.Group == QueueGroup.Construction && wakeWhenConstructionQueueClears
                ? port.ResolveSmartSleepConstructionQueueClearDelay(item, now)
                : null;
            if (queueClearDelay is { } clearDelay && clearDelay > TimeSpan.Zero)
            {
                var queueClearDeadline = now.Add(clearDelay);
                candidateDeadlines.Add(item.NextAttemptAt > queueClearDeadline
                    ? item.NextAttemptAt
                    : queueClearDeadline);
            }
            else
            {
                if (item.NextAttemptAt > now)
                {
                    candidateDeadlines.Add(item.NextAttemptAt);
                }

                if (item.Group == QueueGroup.Construction)
                {
                    AddPositiveDelay(candidateDeadlines, now, port.ResolveConstructionQueueDelay(item, now));
                }
            }

            if (item.Group == QueueGroup.Construction)
            {
                AddPositiveDelay(candidateDeadlines, now, port.ResolveConstructPrerequisiteDelay(item, now));
            }
        }

        return ContinuousLoopForecastPlanner.Resolve(
            scopedItems,
            now,
            candidateDeadlines,
            evaluationTime => port.SelectPreview(evaluationTime, villageKeyFilter, queueItems),
            selected => selected.Group != QueueGroup.Construction
                || port.HasKnownConstructionAvailability(selected, now));
    }

    private static void AddPositiveDelay(
        ISet<DateTimeOffset> candidateDeadlines,
        DateTimeOffset now,
        TimeSpan? delay)
    {
        if (delay is { } value && value > TimeSpan.Zero)
        {
            candidateDeadlines.Add(now + value);
        }
    }
}
