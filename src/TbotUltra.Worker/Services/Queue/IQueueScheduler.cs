using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public interface IQueueScheduler
{
    QueueItem? SelectNext(IReadOnlyList<QueueItem> items);
    IReadOnlyList<QueueItem> OrderForDisplay(IReadOnlyList<QueueItem> items);
}
