using System.Collections.Generic;
using System.Linq;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Picks the next queue item to run with per-village rotation: the runner drains one village's ready
/// tasks before moving on to the next village. When the current village has no ready task left (its
/// remaining tasks are all deferred/waiting), rotation advances to the next village that does have a
/// ready task — so a village waiting for resources never blocks the others.
///
/// The input must already be in the scheduler's display order (priority desc, then FIFO); rotation
/// only changes WHICH village is drained first, never the priority/FIFO order within a village.
/// </summary>
public static class QueueVillageRotation
{
    /// <param name="displayOrderedItems">Queue items in scheduler display order.</param>
    /// <param name="now">Current time; an item is ready when Pending and NextAttemptAt is due.</param>
    /// <param name="villageKeyOf">Resolves an item's village key (null/empty = no specific village).</param>
    /// <param name="isVillageEnabled">
    /// Whether an item's village is enabled for automation; disabled villages' items are skipped.
    /// </param>
    /// <param name="rotationVillageKey">
    /// The village currently being drained; updated in place when rotation advances to a new village.
    /// </param>
    public static QueueItem? SelectNext(
        IReadOnlyList<QueueItem> displayOrderedItems,
        DateTimeOffset now,
        Func<QueueItem, string?> villageKeyOf,
        Func<QueueItem, bool> isVillageEnabled,
        ref string? rotationVillageKey)
    {
        var ready = displayOrderedItems
            .Where(item => item.Status == QueueStatus.Pending
                && item.NextAttemptAt <= now
                && isVillageEnabled(item))
            .ToList();
        if (ready.Count == 0)
        {
            return null;
        }

        // Stay on the current rotation village as long as it still has a ready task. Copy to a local
        // because a ref parameter cannot be captured inside the lambda below.
        var currentKey = rotationVillageKey;
        if (currentKey is not null)
        {
            var sticky = ready.FirstOrDefault(item => KeyEquals(villageKeyOf(item), currentKey));
            if (sticky is not null)
            {
                return sticky;
            }
        }

        // Otherwise advance to the village of the highest-priority ready task and drain that next.
        var pick = ready[0];
        rotationVillageKey = NormalizeKey(villageKeyOf(pick));
        return pick;
    }

    private static bool KeyEquals(string? a, string? b)
    {
        return string.Equals(NormalizeKey(a), NormalizeKey(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? string.Empty : key;
    }
}
