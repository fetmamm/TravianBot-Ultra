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

    // A short-lived, UI-thread-only snapshot of the queue. Every display reader (per-second countdowns,
    // execution-state indicator, automation-loop cards, next-task, village overview) used to call
    // _botService.GetQueueItemsForDisplay() independently, and each call reads queue.json from disk under a
    // file lock. Firing many of those within the same UI tick caused disk churn and lock contention with the
    // worker loop that writes the file — the source of the stuttering/laggy timers. This caches the result
    // for ~120 ms so a tick's burst of display reads share one disk read. Background/loop threads and
    // user-action paths keep calling the store directly (always fresh) — they must never use this cache, so
    // off the UI thread it falls through to a direct read. 120 ms is short enough that the display never
    // lags real state noticeably.
    private IReadOnlyList<QueueItem> GetQueueSnapshotForUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            return _botService.GetQueueItemsForDisplay();
        }

        var now = DateTimeOffset.UtcNow;
        if (_uiQueueSnapshot is null || (now - _uiQueueSnapshotAtUtc).TotalMilliseconds > 120)
        {
            _uiQueueSnapshot = _botService.GetQueueItemsForDisplay();
            _uiQueueSnapshotAtUtc = now;
        }

        return _uiQueueSnapshot;
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

    // Removes pending/deferred upgrade_troops_at_smithy items, optionally for a single village (by name).
    // Called when the user changes a village's Smithy selection: an already-queued item carries the OLD
    // troop snapshot in its payload, and the loop won't enqueue a fresh one while it is still active
    // (HasActiveTaskForVillage dedup), so the new selection would never run. Dropping the stale item lets
    // the loop re-enqueue with the updated targets. Pass null to clear smithy items for every village.
    private int RemoveSmithyQueueItemsForVillage(string? villageName)
        => RemoveQueueItemsForVillage("upgrade_troops_at_smithy", villageName);

    // Same rationale as the Smithy version, for build_troops: a queued item carries the OLD per-village
    // troop-training snapshot, and the loop won't re-enqueue while it is active, so drop it after the user
    // changes that village's training settings. Pass null to clear build_troops items for every village.
    private int RemoveTroopTrainingQueueItemsForVillage(string? villageName)
        => RemoveQueueItemsForVillage("build_troops", villageName);

    private int RemoveQueueItemsForVillage(string taskName, string? villageName)
    {
        var targetName = NormalizeVillageName(villageName);
        var removedCount = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay()
            .Where(item => string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Paused)
            .ToList())
        {
            if (targetName is not null
                && !string.Equals(NormalizeVillageName(GetQueueItemVillageName(item)), targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
