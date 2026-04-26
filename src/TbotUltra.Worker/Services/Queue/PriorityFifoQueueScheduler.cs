using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class PriorityFifoQueueScheduler : IQueueScheduler
{
    public QueueItem? SelectNext(IReadOnlyList<QueueItem> items)
    {
        var now = DateTimeOffset.UtcNow;
        return Order(items)
            .FirstOrDefault(item =>
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt <= now);
    }

    public IReadOnlyList<QueueItem> OrderForDisplay(IReadOnlyList<QueueItem> items)
    {
        return Order(items).ToList();
    }

    private static IEnumerable<QueueItem> Order(IReadOnlyList<QueueItem> items)
    {
        return items
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt);
    }
}
