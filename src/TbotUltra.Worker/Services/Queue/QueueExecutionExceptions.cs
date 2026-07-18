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

    /// <summary>
    /// Optional machine-readable reason (see <see cref="TaskWaitReasons"/>). Consumers should read
    /// this instead of sniffing the free-text message, which silently breaks when reworded.
    /// </summary>
    public string? ReasonCode { get; }

    public TaskWaitException(int delaySeconds, string message, string? reasonCode = null)
        : base(message)
    {
        DelaySeconds = delaySeconds <= 0 ? 1 : delaySeconds;
        ReasonCode = reasonCode;
    }
}

/// <summary>
/// Machine-readable wait reasons carried by <see cref="TaskWaitException.ReasonCode"/>. Derived in
/// ONE place (BotTaskRunner.TaskHandlers.DeriveTaskWaitReason) from the clients' result messages,
/// so the message wording and its interpretation cannot drift apart across the codebase.
/// </summary>
public static class TaskWaitReasons
{
    /// <summary>The task performed its work (e.g. troops queued) and defers only for the cooldown.</summary>
    public const string WorkQueued = "work_queued";

    /// <summary>hero_manage deferred because the hero is reviving.</summary>
    public const string HeroReviving = "hero_reviving";

    /// <summary>hero_manage deferred because the hero is travelling/away.</summary>
    public const string HeroAway = "hero_away";

    /// <summary>hero_manage deferred because the hero's HP is below the adventure threshold.</summary>
    public const string HeroHpTooLow = "hero_hp_too_low";
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
