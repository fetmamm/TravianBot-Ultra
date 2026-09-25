using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IContinuousAutomationPassRuntime
{
    long BeginPass();
    TimeSpan NetworkBackoffRemaining { get; }
    DateTimeOffset NextKeepAliveAtUtc { get; }
    bool PrioritizeDeadlineWorkOnWake { get; set; }
    bool HasPendingLoginRound { get; }
    QueueItem? SelectReadyPriorityQueueItem(BotOptions options);
    bool ConsumeImmediateWorkRequest();
    ValueTask MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask MaybeDoIdleBrowseAsync(BotOptions options, CancellationToken cancellationToken);
    bool ConsumeForceVillageStatusRoundRequest();
    ValueTask MaybeRunVillageStatusRoundAsync(BotOptions options, CancellationToken cancellationToken, bool force);
    ValueTask EnsureRuntimeItemsAsync(BotOptions options, CancellationToken cancellationToken);
    QueueItem? SelectNextQueueItem();
    void MarkActivePass();
    ContinuousAutomationDeadlineSnapshot ReadDeadlines(BotOptions options);
    bool ShouldPublishIdleHeartbeat(TimeSpan interval);
}

internal sealed class ContinuousAutomationPassRuntime(
    AutomationPassRuntime pass,
    AutomationNetworkBackoff networkBackoff,
    AutomationSessionRuntime session,
    AutomationQueueSelectionCoordinator queueSelection,
    ContinuousAutomationDeadlineCoordinator deadlines,
    ContinuousRuntimeItemPreparation runtimeItemPreparation,
    ContinuousIdlePacing idlePacing,
    VillageStatusRoundRuntime villageStatusRoundState,
    ContinuousVillageStatusRound villageStatusRound) : IContinuousAutomationPassRuntime
{
    public long BeginPass() => pass.BeginContinuousPass();
    public TimeSpan NetworkBackoffRemaining => networkBackoff.Remaining;
    public DateTimeOffset NextKeepAliveAtUtc => session.NextKeepAliveAtUtc;
    public bool PrioritizeDeadlineWorkOnWake
    {
        get => pass.PrioritizeDeadlineWorkOnWake;
        set => pass.PrioritizeDeadlineWorkOnWake = value;
    }
    public bool HasPendingLoginRound => villageStatusRound.LoginRoundPending;
    public QueueItem? SelectReadyPriorityQueueItem(BotOptions options) =>
        queueSelection.SelectUrgent(options, new HashSet<Guid>(), explicitPriorityOnly: true);
    public bool ConsumeImmediateWorkRequest() => pass.ConsumeImmediateWorkRequest();
    public ValueTask MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken cancellationToken) =>
        idlePacing.MaybeTakeBreakAsync(options, cancellationToken);
    public ValueTask MaybeDoIdleBrowseAsync(BotOptions options, CancellationToken cancellationToken) =>
        idlePacing.MaybeBrowseAsync(options, cancellationToken);
    public bool ConsumeForceVillageStatusRoundRequest() => villageStatusRoundState.ConsumeForceRequest();
    public ValueTask MaybeRunVillageStatusRoundAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        bool force) => villageStatusRound.RunIfDueAsync(options, cancellationToken, force);
    public ValueTask EnsureRuntimeItemsAsync(BotOptions options, CancellationToken cancellationToken) =>
        runtimeItemPreparation.PrepareAsync(options, cancellationToken);
    public QueueItem? SelectNextQueueItem() => queueSelection.Select();
    public void MarkActivePass() => session.MarkActivePass();
    public ContinuousAutomationDeadlineSnapshot ReadDeadlines(BotOptions options) => deadlines.Read(options);
    public bool ShouldPublishIdleHeartbeat(TimeSpan interval) => session.ShouldPublishIdleHeartbeat(interval);
}

internal interface IAutoQueueAutomationPassRuntime
{
    long RunLogId { get; }
    bool PrioritizeDeadlineWorkOnWake { get; set; }
    bool HasPendingLoginRound { get; }
    QueueItem? SelectReadyPriorityQueueItem(BotOptions options);
    ValueTask RunPendingLoginRoundAsync(BotOptions options, CancellationToken cancellationToken);
    QueueItem? SelectNextQueueItem();
    IReadOnlyList<QueueItem> GetEligibleQueueItems();
    IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups { get; }
    IReadOnlyDictionary<Guid, DateTimeOffset> GetSmartSleepQueueDeadlineOverrides(
        IReadOnlyList<QueueItem> items,
        DateTimeOffset now);
}

internal sealed class AutoQueueAutomationPassRuntime(
    AutomationPassRuntime pass,
    AutomationQueueSelectionCoordinator queueSelection,
    ContinuousAutomationDeadlineCoordinator deadlines,
    ContinuousVillageStatusRound villageStatusRound) : IAutoQueueAutomationPassRuntime
{
    public long RunLogId => pass.AutoQueueRunLogId;
    public bool PrioritizeDeadlineWorkOnWake
    {
        get => pass.PrioritizeDeadlineWorkOnWake;
        set => pass.PrioritizeDeadlineWorkOnWake = value;
    }
    public bool HasPendingLoginRound => villageStatusRound.LoginRoundPending;
    public QueueItem? SelectReadyPriorityQueueItem(BotOptions options) =>
        queueSelection.SelectUrgent(options, new HashSet<Guid>(), explicitPriorityOnly: true);
    public ValueTask RunPendingLoginRoundAsync(BotOptions options, CancellationToken cancellationToken) =>
        villageStatusRound.RunIfDueAsync(
            AutomationExecutionOptions.WithoutImplicitVillageTarget(options),
            cancellationToken);
    public QueueItem? SelectNextQueueItem() => queueSelection.Select();
    public IReadOnlyList<QueueItem> GetEligibleQueueItems() => queueSelection.GetEligibleItems();
    public IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups => pass.SmartSleepDeadlineGroups;
    public IReadOnlyDictionary<Guid, DateTimeOffset> GetSmartSleepQueueDeadlineOverrides(
        IReadOnlyList<QueueItem> items,
        DateTimeOffset now) => deadlines.ResolveQueueDeadlineOverrides(items, now);
}
