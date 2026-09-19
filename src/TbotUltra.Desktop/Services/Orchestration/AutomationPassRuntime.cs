using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed class AutomationPassRuntime
{
    private readonly VillageBatchState _villageBatch = new();
    private long _nextContinuousPassId;
    private long _currentContinuousPassId;
    private long _autoQueueRunLogId;
    private int _immediateWorkRequested;
    private int _prioritizeDeadlineWorkOnWake;
    private IReadOnlySet<QueueGroup> _smartSleepDeadlineGroups =
        SmartSleepDeadlinePolicy.DefaultGroups.ToHashSet();

    internal long BeginContinuousPass()
    {
        var passId = Interlocked.Increment(ref _nextContinuousPassId);
        Interlocked.Exchange(ref _currentContinuousPassId, passId);
        return passId;
    }

    internal long CurrentContinuousPassId => Volatile.Read(ref _currentContinuousPassId);

    internal long AutoQueueRunLogId => Volatile.Read(ref _autoQueueRunLogId);

    internal void BeginAutoQueueRun(long logId)
    {
        ResetVillageBatch();
        Interlocked.Exchange(ref _autoQueueRunLogId, logId);
    }

    internal void RequestImmediateWork() => Interlocked.Exchange(ref _immediateWorkRequested, 1);

    internal bool ConsumeImmediateWorkRequest() =>
        Interlocked.Exchange(ref _immediateWorkRequested, 0) == 1;

    internal bool IsImmediateWorkRequested => Volatile.Read(ref _immediateWorkRequested) == 1;

    internal bool PrioritizeDeadlineWorkOnWake
    {
        get => Volatile.Read(ref _prioritizeDeadlineWorkOnWake) == 1;
        set => Interlocked.Exchange(ref _prioritizeDeadlineWorkOnWake, value ? 1 : 0);
    }

    internal IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups => _smartSleepDeadlineGroups;

    internal void SetSmartSleepDeadlineGroups(IReadOnlySet<QueueGroup> groups) =>
        _smartSleepDeadlineGroups = groups.ToHashSet();

    internal VillageBatchSnapshot SnapshotVillageBatch(string? verifiedVillageKey) =>
        _villageBatch.SnapshotFor(verifiedVillageKey);

    internal void ObserveVerifiedVillage(string? villageKey) =>
        _villageBatch.ObserveVerifiedVillage(villageKey);

    internal VillageBatchSnapshot RecordVillageAttempt(
        string? targetVillageKey,
        string? verifiedVillageKey) =>
        _villageBatch.RecordAttempt(targetVillageKey, verifiedVillageKey);

    internal void RecordUrgentPreemption(string? currentVillageKey, string? targetVillageKey) =>
        _villageBatch.RecordUrgentPreemption(currentVillageKey, targetVillageKey);

    internal void CompleteUrgentPreemption(string? verifiedVillageKey) =>
        _villageBatch.CompleteUrgentPreemption(verifiedVillageKey);

    internal void ResetVillageBatch() => _villageBatch.Reset();
}
