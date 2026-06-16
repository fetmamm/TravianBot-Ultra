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
