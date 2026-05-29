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
