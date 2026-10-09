using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public interface IQueueStore
{
    IReadOnlyList<QueueItem> GetAll();
    void Clear();
    QueueItem Add(string taskName, Dictionary<string, string>? payload, int priority, int maxRetries);
    IReadOnlyList<QueueItem> AddBatch(IReadOnlyList<QueueItemCreateRequest> requests);
    IReadOnlyList<QueueItem> ReplaceActiveGroup(QueueGroup group, IReadOnlyList<QueueItemCreateRequest> requests);
    QueueItem AddRuntime(string taskName, string displayName, Dictionary<string, string>? payload, int priority, int maxRetries);
    bool Remove(Guid id);
    int RemoveMany(IReadOnlyCollection<Guid> ids);
    bool MoveUp(Guid id);
    bool MoveDown(Guid id);
    bool MoveToTop(Guid id);
    bool MoveToBottom(Guid id);
    bool ApplyOrder(IReadOnlyList<Guid> orderedIds);
    bool Pause(Guid id);
    bool Resume(Guid id);
    bool Retry(Guid id);
    bool MarkRunning(Guid id);
    bool MarkSucceeded(Guid id);
    bool MarkCanceled(Guid id);
    bool MarkDeferred(Guid id, TimeSpan delay, IReadOnlyDictionary<string, string>? valuesToSet = null);
    bool UpdateDeferred(Guid id, Dictionary<string, string>? payload, TimeSpan? delay = null);
    bool PatchDeferred(
        Guid id,
        IReadOnlyDictionary<string, string>? valuesToSet,
        IReadOnlyCollection<string>? keysToRemove,
        TimeSpan? delay = null,
        string? expectedDeferReason = null);
    bool UpdatePending(Guid id, Dictionary<string, string>? payload, int? priority, TimeSpan? delay = null);
    bool ApplyPendingReconciliation(IReadOnlyList<Guid> removals, IReadOnlyList<QueuePayloadUpdate> updates);
    bool MarkExecutionFailed(Guid id);
    bool MarkPermanentlyFailed(Guid id);
    int ResetOrphanedRunningItems();
}
