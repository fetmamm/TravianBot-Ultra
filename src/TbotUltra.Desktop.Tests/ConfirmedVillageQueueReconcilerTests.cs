using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConfirmedVillageQueueReconcilerTests
{
    [Fact]
    public void PausePendingItemsForMissingVillages_PausesOnlyPendingTargetedMissingItems()
    {
        var live = Item("name:live", QueueStatus.Pending);
        var missingPending = Item("name:missing", QueueStatus.Pending);
        var missingSucceeded = Item("name:missing", QueueStatus.Succeeded);
        var villageLess = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "hero_manage",
            Status = QueueStatus.Pending,
        };
        var pausedIds = new List<Guid>();

        var paused = ConfirmedVillageQueueReconciler.PausePendingItemsForMissingVillages(
            new[] { live, missingPending, missingSucceeded, villageLess },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name:live" },
            item => item.Payload.TryGetValue("villageKey", out var key) ? key : null,
            id =>
            {
                pausedIds.Add(id);
                return true;
            });

        Assert.Equal(1, paused);
        Assert.Equal(missingPending.Id, Assert.Single(pausedIds));
    }

    [Fact]
    public void RemoveItemsForVillages_RemovesOnlyPendingAndPausedItemsOfRemovedVillages()
    {
        var removedPending = Item("xy:1|1", QueueStatus.Pending);
        var removedPaused = Item("xy:1|1", QueueStatus.Paused);
        var removedRunning = Item("xy:1|1", QueueStatus.Running);
        var removedSucceeded = Item("xy:1|1", QueueStatus.Succeeded);
        var liveVillage = Item("xy:2|2", QueueStatus.Pending);
        var removedIds = new List<Guid>();

        var removed = ConfirmedVillageQueueReconciler.RemoveItemsForVillages(
            new[] { removedPending, removedPaused, removedRunning, removedSucceeded, liveVillage },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xy:1|1" },
            item => item.Payload.TryGetValue("villageKey", out var key) ? key : null,
            id =>
            {
                removedIds.Add(id);
                return true;
            });

        Assert.Equal(2, removed);
        Assert.Contains(removedPending.Id, removedIds);
        Assert.Contains(removedPaused.Id, removedIds);
        Assert.DoesNotContain(removedRunning.Id, removedIds);
        Assert.DoesNotContain(removedSucceeded.Id, removedIds);
        Assert.DoesNotContain(liveVillage.Id, removedIds);
    }

    private static QueueItem Item(string villageKey, QueueStatus status)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "upgrade_building_to_level",
            Status = status,
            Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["villageKey"] = villageKey,
            },
        };
    }
}
