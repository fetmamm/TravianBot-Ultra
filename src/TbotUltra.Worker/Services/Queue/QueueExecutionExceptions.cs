using System;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Signals that a queued task cannot make progress right now (e.g. waiting for resources,
/// build queue full, cooldown active) but should be retried later WITHOUT counting against
/// MaxRetries. The worker re-schedules the item with <see cref="DelaySeconds"/> using
/// <see cref="Queue.IQueueStore.MarkDeferred"/>.
/// </summary>
public sealed class TaskWaitException : Exception
{
    public int DelaySeconds { get; }

    public TaskWaitException(int delaySeconds, string message)
        : base(message)
    {
        DelaySeconds = delaySeconds <= 0 ? 1 : delaySeconds;
    }
}

/// <summary>
/// Signals that a queued task can never succeed in its current form (e.g. building reports
/// max level reached, required building missing). The worker marks the item as Failed
/// immediately rather than burning the full retry budget on an impossible request.
/// </summary>
public sealed class TaskBlockedPermanentlyException : Exception
{
    public TaskBlockedPermanentlyException(string message)
        : base(message)
    {
    }
}
