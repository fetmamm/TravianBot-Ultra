using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed record ConstructionQueueSelection(
    QueueItem? Item,
    string? SkipReason,
    QueueItem? QueueFullBlocker,
    bool ForcedLiveValidation,
    bool UsedIndependentCategoryLookAhead = false);

public static class ConstructionQueueSelector
{
    public static ConstructionQueueSelection SelectNext(
        IReadOnlyList<QueueItem> orderedItems,
        DateTimeOffset now,
        ConstructionQueueAvailability availability,
        Func<int, bool>? isBlockedByEarlierDependency = null,
        Func<int, ConstructionQueueAvailability>? availabilityForIndex = null,
        bool allowIndependentCategoryLookAhead = false)
    {
        if (orderedItems.Count == 0)
        {
            return new ConstructionQueueSelection(
                null,
                "group=Construction skipped (no pending/running/paused items)",
                null,
                false);
        }

        // A one-level construction task is marked InProgress after its click is registered. While its
        // authoritative retry deadline is still ahead it must not hold later rows when Travian has a free
        // slot (for example MB6 may be followed by MB7 on a Plus queue). Once due, select the item itself
        // so the Worker revalidates its live level; otherwise a stale in-progress row can block every
        // dependent template row even after a confirmed empty construction overview.
        var itemIndex = 0;
        while (itemIndex < orderedItems.Count
            && CanYieldQueueOrderAfterInProgress(orderedItems[itemIndex], now))
        {
            itemIndex++;
        }

        if (itemIndex >= orderedItems.Count)
        {
            return new ConstructionQueueSelection(
                null,
                "group=Construction has only in-progress single-level tasks; waiting for the next queue refresh",
                null,
                false);
        }

        var item = orderedItems[itemIndex];
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

                var lookAhead = TrySelectIndependentCategoryLane(
                    orderedItems,
                    itemIndex,
                    now,
                    allowIndependentCategoryLookAhead,
                    isBlockedByEarlierDependency,
                    availabilityForIndex);
                if (lookAhead is not null)
                {
                    return lookAhead;
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

        var itemAvailability = availabilityForIndex?.Invoke(itemIndex) ?? availability;
        if (itemAvailability == ConstructionQueueAvailability.Full)
        {
            var lookAhead = TrySelectIndependentCategoryLane(
                orderedItems,
                itemIndex,
                now,
                allowIndependentCategoryLookAhead,
                isBlockedByEarlierDependency,
                availabilityForIndex);
            if (lookAhead is not null)
            {
                return lookAhead;
            }

            return new ConstructionQueueSelection(
                null,
                $"group=Construction task='{item.TaskName}' blocked by live full build queue; holding queue order",
                item,
                false);
        }

        if (isBlockedByEarlierDependency?.Invoke(itemIndex) == true)
        {
            return new ConstructionQueueSelection(
                null,
                $"group=Construction task='{item.TaskName}' blocked by an earlier dependency; holding queue order",
                null,
                false);
        }

        return new ConstructionQueueSelection(item, null, null, false);
    }

    private static ConstructionQueueSelection? TrySelectIndependentCategoryLane(
        IReadOnlyList<QueueItem> orderedItems,
        int blockedIndex,
        DateTimeOffset now,
        bool allowIndependentCategoryLookAhead,
        Func<int, bool>? isBlockedByEarlierDependency,
        Func<int, ConstructionQueueAvailability>? availabilityForIndex)
    {
        if (!allowIndependentCategoryLookAhead || availabilityForIndex is null)
        {
            return null;
        }

        var blockedIsResource = ConstructionQueueState.IsResourceConstructionTask(
            orderedItems[blockedIndex].TaskName);
        for (var index = blockedIndex + 1; index < orderedItems.Count; index++)
        {
            var candidate = orderedItems[index];
            if (ConstructionQueueState.IsResourceConstructionTask(candidate.TaskName) == blockedIsResource)
            {
                continue;
            }

            // A started one-level task may yield within its own category just as it does at the
            // normal queue head. Every other row remains the head of this category lane.
            if (CanYieldQueueOrderAfterInProgress(candidate, now))
            {
                continue;
            }

            if (candidate.Status != QueueStatus.Pending
                || candidate.NextAttemptAt > now
                || availabilityForIndex(index) != ConstructionQueueAvailability.Available
                || isBlockedByEarlierDependency?.Invoke(index) == true)
            {
                return null;
            }

            return new ConstructionQueueSelection(candidate, null, null, false, true);
        }

        return null;
    }

    private static bool CanYieldQueueOrderAfterInProgress(QueueItem item, DateTimeOffset now)
    {
        if (!ConstructionQueueState.IsConstructionInProgressDeferred(item)
            || item.NextAttemptAt <= now)
        {
            return false;
        }

        return string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase);
    }
}
