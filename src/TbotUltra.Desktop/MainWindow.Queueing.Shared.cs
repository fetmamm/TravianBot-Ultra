using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private const double SlotClickCooldownMilliseconds = 120;

    private static bool IsActiveQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Paused or QueueStatus.Running;
    }

    private int RemoveCoalescedQueueItems(IEnumerable<QueueItem> candidates, Action<QueueItem>? onRemoved = null)
    {
        var removedCount = 0;
        foreach (var item in candidates.Where(item => item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(item.Id))
            {
                onRemoved?.Invoke(item);
                removedCount += 1;
            }
        }

        return removedCount;
    }

    private int ClearQueuePreservingDeferredHeroTimers()
    {
        var now = DateTimeOffset.UtcNow;
        var preservedCount = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay().ToList())
        {
            if (IsDeferredHeroManageTimer(item, now))
            {
                preservedCount += 1;
                continue;
            }

            _botService.RemoveQueueItem(item.Id);
        }

        RequestQueueUiRefresh();
        return preservedCount;
    }

    private int ClearHeroManageQueueItems()
    {
        var removedCount = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay()
            .Where(IsHeroManageQueueItem)
            .ToList())
        {
            if (_botService.RemoveQueueItem(item.Id))
            {
                removedCount += 1;
            }
        }

        if (removedCount > 0)
        {
            RequestQueueUiRefresh();
        }

        return removedCount;
    }

    private static bool IsDeferredHeroManageTimer(QueueItem item, DateTimeOffset now)
    {
        return IsHeroManageQueueItem(item)
            && item.Group == QueueGroup.Hero
            && item.Status == QueueStatus.Pending
            && item.NextAttemptAt > now;
    }

    private static bool IsHeroManageQueueItem(QueueItem item)
    {
        return string.Equals(item.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBeginSlotClick(Dictionary<int, DateTimeOffset> cooldowns, int slotId, DateTimeOffset now)
    {
        if (cooldowns.TryGetValue(slotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < SlotClickCooldownMilliseconds)
        {
            return false;
        }

        cooldowns[slotId] = now;
        return true;
    }
}
