using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class ConfirmedVillageQueueReconciler
{
    public static int PausePendingItemsForMissingVillages(
        IReadOnlyList<QueueItem> items,
        IReadOnlySet<string> confirmedLiveVillageKeys,
        Func<QueueItem, string?> villageKeyOf,
        Func<Guid, bool> pauseItem)
    {
        if (items.Count == 0 || confirmedLiveVillageKeys.Count == 0)
        {
            return 0;
        }

        var paused = 0;
        foreach (var item in items)
        {
            if (item.Status != QueueStatus.Pending)
            {
                continue;
            }

            var villageKey = villageKeyOf(item);
            if (string.IsNullOrWhiteSpace(villageKey) || confirmedLiveVillageKeys.Contains(villageKey))
            {
                continue;
            }

            if (pauseItem(item.Id))
            {
                paused++;
            }
        }

        return paused;
    }

    /// <summary>
    /// Removes the lingering queue items that belong to villages confirmed lost/destroyed (their keys are
    /// in <paramref name="removedVillageKeys"/>). Only non-terminal junk is removed (Pending/Paused); a
    /// Running item is left untouched, and Succeeded/Failed/Canceled history is kept. Returns the count
    /// removed. Must be called BEFORE the village records are dropped so name-based key resolution still
    /// maps legacy items to the removed village.
    /// </summary>
    public static int RemoveItemsForVillages(
        IReadOnlyList<QueueItem> items,
        IReadOnlySet<string> removedVillageKeys,
        Func<QueueItem, string?> villageKeyOf,
        Func<Guid, bool> removeItem)
    {
        if (items.Count == 0 || removedVillageKeys.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        foreach (var item in items)
        {
            if (item.Status is not (QueueStatus.Pending or QueueStatus.Paused))
            {
                continue;
            }

            var villageKey = villageKeyOf(item);
            if (string.IsNullOrWhiteSpace(villageKey) || !removedVillageKeys.Contains(villageKey))
            {
                continue;
            }

            if (removeItem(item.Id))
            {
                removed++;
            }
        }

        return removed;
    }
}
