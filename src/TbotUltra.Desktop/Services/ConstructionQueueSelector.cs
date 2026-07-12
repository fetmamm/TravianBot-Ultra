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
                    // Current queue-full defers already contain an authoritative retry time from the
                    // worker's live slot read. Do not override that time from a ticking desktop cache:
                    // Romans have separate resource/building capacity, which a village-wide active count
                    // cannot distinguish. Only legacy items without the current classification may need
                    // one early live validation to migrate them onto the reliable path.
                    var shouldValidateNow = ConstructionQueueState.IsLegacyQueueOccupancyDeferred(item)
                        && availability != ConstructionQueueAvailability.Full
                        && queueFullBlocker is null;
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

                if (ConstructionQueueState.IsStorageCapacityDeferred(item))
                {
                    continue;
                }

                if (ConstructionQueueState.IsConstructionRequirementDeferred(item))
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
