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
}
