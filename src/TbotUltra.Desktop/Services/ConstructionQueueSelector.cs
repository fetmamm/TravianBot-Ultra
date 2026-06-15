using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed record ConstructionQueueSelection(
    QueueItem? Item,
    string? SkipReason,
    QueueItem? QueueFullBlocker,
    bool ForcedLiveValidation);

public static class ConstructionQueueSelector
{
    public static ConstructionQueueSelection SelectNext(
        IReadOnlyList<QueueItem> orderedItems,
        DateTimeOffset now,
        ConstructionQueueAvailability availability,
        Func<int, bool>? isBlockedByEarlierDependency = null)
    {
        if (orderedItems.Count == 0)
        {
            return new ConstructionQueueSelection(
                null,
                "group=Construction skipped (no pending/running/paused items)",
                null,
                false);
        }

        QueueItem? queueFullBlocker = null;
        for (var index = 0; index < orderedItems.Count; index++)
        {
            var item = orderedItems[index];
            if (item.Status != QueueStatus.Pending)
            {
                return new ConstructionQueueSelection(
                    null,
                    $"group=Construction task='{item.TaskName}' is {item.Status} (not Pending)",
                    queueFullBlocker,
                    false);
            }

            if (item.NextAttemptAt > now)
            {
                if (ConstructionQueueState.IsQueueOccupancyDeferred(item))
                {
                    var shouldValidateNow = availability == ConstructionQueueAvailability.Available
                        || (availability == ConstructionQueueAvailability.Unknown
                            && ConstructionQueueState.IsLegacyQueueOccupancyDeferred(item)
                            && queueFullBlocker is null);
                    if (shouldValidateNow)
                    {
                        return new ConstructionQueueSelection(item, null, null, true);
                    }

                    queueFullBlocker ??= item;
                    continue;
                }

                if (ConstructionQueueState.IsConstructionInProgressDeferred(item))
                {
                    continue;
                }

                var waitSeconds = Math.Max(0, (item.NextAttemptAt - now).TotalSeconds);
                return new ConstructionQueueSelection(
                    null,
                    $"group=Construction task='{item.TaskName}' waiting {waitSeconds:F0}s; holding queue order",
                    queueFullBlocker,
                    false);
            }

            if (queueFullBlocker is not null)
            {
                continue;
            }

            if (isBlockedByEarlierDependency?.Invoke(index) == true)
            {
                continue;
            }

            return new ConstructionQueueSelection(item, null, null, false);
        }

        if (queueFullBlocker is not null)
        {
            var waitSeconds = Math.Max(0, (queueFullBlocker.NextAttemptAt - now).TotalSeconds);
            return new ConstructionQueueSelection(
                null,
                $"group=Construction build queue full; next validation in {waitSeconds:F0}s",
                queueFullBlocker,
                false);
        }

        return new ConstructionQueueSelection(
            null,
            "group=Construction skipped (no ready construction items)",
            null,
            false);
    }
}
