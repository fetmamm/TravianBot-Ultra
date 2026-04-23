using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public interface IQueueStore
{
    IReadOnlyList<QueueItem> GetAll();
    void Clear();
    QueueItem Add(string taskName, Dictionary<string, string>? payload, int priority, int maxRetries);
    bool Remove(Guid id);
    bool MoveUp(Guid id);
    bool MoveDown(Guid id);
    bool Pause(Guid id);
    bool Resume(Guid id);
    bool Retry(Guid id);
    bool MarkRunning(Guid id);
    bool MarkSucceeded(Guid id);
    bool MarkDeferred(Guid id, TimeSpan delay);
    bool MarkExecutionFailed(Guid id);
}
