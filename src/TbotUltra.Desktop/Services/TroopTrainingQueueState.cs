using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Keeps the per-building troop-training queue as the source of truth for the dashboard timers: once a
/// queue is read, it stays and ticks down on its absolute <see cref="TimerSnapshot"/> finish instead of
/// vanishing when a later (partial / off-village) read misses it. Mirrors
/// <see cref="SmithyQueueState"/> / <see cref="ConstructionQueueState.PreserveKnownConstructionState"/>.
/// A queue is only dropped when its finish has actually elapsed, or a fresh read shows a real new queue.
/// </summary>
public static class TroopTrainingQueueState
{
    public static IReadOnlyList<TroopTrainingQueueStatus> PreserveKnownActiveQueue(
        IReadOnlyList<TroopTrainingQueueStatus>? incoming,
        IReadOnlyList<TroopTrainingQueueStatus>? existing,
        DateTimeOffset now)
    {
        if (incoming is null)
        {
            return existing ?? [];
        }

        if (existing is null || existing.Count == 0)
        {
            return incoming;
        }

        return incoming
            .Select(item => PreserveOne(item, existing, now))
            .ToList();
    }

    private static TroopTrainingQueueStatus PreserveOne(
        TroopTrainingQueueStatus incoming,
        IReadOnlyList<TroopTrainingQueueStatus> existing,
        DateTimeOffset now)
    {
        // The fresh read already has a live queue for this building — trust it.
        if (HasUnfinishedQueue(incoming, now))
        {
            return incoming;
        }

        var prior = existing.FirstOrDefault(e => e is not null && e.BuildingType == incoming.BuildingType);
        if (prior is null || !HasUnfinishedQueue(prior, now))
        {
            return incoming;
        }

        // Fresh read lost the queue but the cached one is still ticking — keep the known queue/timer so the
        // UI countdown survives village switches and partial reads.
        var remaining = prior.Finish?.RemainingSecondsAt(now) ?? prior.RemainingSeconds;
        return incoming with
        {
            Exists = incoming.Exists || prior.Exists,
            SlotId = incoming.SlotId ?? prior.SlotId,
            QueueItems = prior.QueueItems,
            RemainingSeconds = remaining,
            RemainingText = prior.RemainingText,
            Finish = prior.Finish,
        };
    }

    private static bool HasUnfinishedQueue(TroopTrainingQueueStatus? status, DateTimeOffset now)
    {
        if (status is null)
        {
            return false;
        }

        if (status.Finish is not null)
        {
            return !status.Finish.IsFinishedAt(now);
        }

        return status.RemainingSeconds is > 0;
    }
}
