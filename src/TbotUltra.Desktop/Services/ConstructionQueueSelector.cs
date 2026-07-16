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

        // Construction is strictly ordered per village: only the visible head item may run.
        // Requirement repair is the sole mechanism allowed to promote or insert a prerequisite
        // ahead of it, after which that repaired item becomes the new head.
        var item = orderedItems[0];
        if (item.Status != QueueStatus.Pending)
        {
            return new ConstructionQueueSelection(
                null,
                $"group=Construction task='{item.TaskName}' is {item.Status} (not Pending)",
                null,
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
                    && availability != ConstructionQueueAvailability.Full;
                if (shouldValidateNow)
                {
                    return new ConstructionQueueSelection(item, null, null, true);
                }

                var queueWaitSeconds = Math.Max(0, (item.NextAttemptAt - now).TotalSeconds);
                return new ConstructionQueueSelection(
                    null,
                    $"group=Construction build queue full; next validation in {queueWaitSeconds:F0}s; holding queue order",
                    item,
                    false);
            }

            var waitSeconds = Math.Max(0, (item.NextAttemptAt - now).TotalSeconds);
            return new ConstructionQueueSelection(
                null,
                $"group=Construction task='{item.TaskName}' waiting {waitSeconds:F0}s; holding queue order",
                null,
                false);
        }

        if (availability == ConstructionQueueAvailability.Full)
        {
            return new ConstructionQueueSelection(
                null,
                $"group=Construction task='{item.TaskName}' blocked by live full build queue; holding queue order",
                item,
                false);
        }

        if (isBlockedByEarlierDependency?.Invoke(0) == true)
        {
            return new ConstructionQueueSelection(
                null,
                $"group=Construction task='{item.TaskName}' blocked by an earlier dependency; holding queue order",
                null,
                false);
        }

        return new ConstructionQueueSelection(item, null, null, false);
    }
}
