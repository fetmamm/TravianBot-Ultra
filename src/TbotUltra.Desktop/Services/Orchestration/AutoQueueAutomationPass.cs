using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutoQueueAutomationPassPort
{
    BotOptions LoadOptionsWithSelectedVillage();
    long RunLogId { get; }
    bool PrioritizeDeadlineWorkOnWake { get; set; }
    bool HasPendingLoginRound { get; }
    QueueItem? SelectReadyPriorityQueueItem(BotOptions options);
    ValueTask RunPendingLoginRoundAsync(BotOptions options, CancellationToken cancellationToken);
    IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups { get; }
    ValueTask HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken cancellationToken);
    QueueItem? SelectNextQueueItem();
    IReadOnlyList<QueueItem> GetQueueItems();
    IReadOnlyDictionary<Guid, DateTimeOffset> GetSmartSleepQueueDeadlineOverrides(
        IReadOnlyList<QueueItem> items,
        DateTimeOffset now);
    bool IsAllowedByAutomationSettings(QueueItem item);
    bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc);
    void Log(string message);
    ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationCandidate action,
        CancellationToken cancellationToken);
}

internal sealed class AutoQueueAutomationPass(
    IAutoQueueAutomationPassPort port,
    TimeProvider? timeProvider = null) : IAutomationModePassPort
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken)
    {
        var options = port.LoadOptionsWithSelectedVillage();
        await port.HonorPendingVillageSwitchAsync(options, cancellationToken);
        if (port.HasPendingLoginRound)
        {
            var priority = port.SelectReadyPriorityQueueItem(options);
            if (priority is not null)
                return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(priority)]);
            await port.RunPendingLoginRoundAsync(options, cancellationToken);
            if (port.HasPendingLoginRound)
                return new AutomationStateSnapshot([], NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(10));
        }

        var selected = port.SelectNextQueueItem();
        if (selected is not null)
        {
            port.PrioritizeDeadlineWorkOnWake = false;
            return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(selected)]);
        }

        var now = _timeProvider.GetUtcNow();
        var eligibleItems = port.GetQueueItems()
            .Where(port.IsAllowedByAutomationSettings)
            .ToList();
        var nextDeferredItem = eligibleItems
            .Where(item => !item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
            .FirstOrDefault(item => item.NextAttemptAt > now)
            ?? eligibleItems
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefault();

        if (nextDeferredItem is null)
        {
            port.Log($"[AUTOQ {port.RunLogId}] DONE (queue empty).");
            return new AutomationStateSnapshot([], IsComplete: true);
        }

        port.Log(
            $"[AUTOQ {port.RunLogId}] WAIT "
            + $"{Math.Max(0, (nextDeferredItem.NextAttemptAt - now).TotalSeconds):F0}s "
            + $"for deferred task={nextDeferredItem.TaskName}");
        var smartSleepDelay = SmartSleepDeadlinePolicy.ResolveNextDelay(
            now,
            eligibleItems,
            port.SmartSleepDeadlineGroups,
            nextConstructionAvailabilityUtc: null,
            queueDeadlineOverrides: port.GetSmartSleepQueueDeadlineOverrides(eligibleItems, now));
        _ = port.TryRequestSmartSleep(smartSleepDelay is { } delay ? now.Add(delay) : null);
        return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(nextDeferredItem)]);
    }

    public ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        CancellationToken cancellationToken) => port.ExecuteAsync(action, cancellationToken);
}
