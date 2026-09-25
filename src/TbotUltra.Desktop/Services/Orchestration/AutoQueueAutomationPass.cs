using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutoQueueAutomationPassPort
{
    BotOptions LoadOptionsWithSelectedVillage();
    ValueTask HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken cancellationToken);
    bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc);
    void Log(string message);
    ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationCandidate action,
        CancellationToken cancellationToken);
}

internal sealed class AutoQueueAutomationPass : IAutomationModePassPort
{
    private readonly IAutoQueueAutomationPassPort _port;
    private readonly IAutoQueueAutomationPassRuntime _runtime;
    private readonly TimeProvider _timeProvider;

    internal AutoQueueAutomationPass(
        IAutoQueueAutomationPassPort port,
        TimeProvider? timeProvider = null)
        : this(
            port,
            port as IAutoQueueAutomationPassRuntime
                ?? throw new ArgumentException("A runtime is required for the auto-queue pass.", nameof(port)),
            timeProvider)
    {
    }

    internal AutoQueueAutomationPass(
        IAutoQueueAutomationPassPort port,
        IAutoQueueAutomationPassRuntime runtime,
        TimeProvider? timeProvider = null)
    {
        _port = port;
        _runtime = runtime;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken)
    {
        var options = _port.LoadOptionsWithSelectedVillage();
        await _port.HonorPendingVillageSwitchAsync(options, cancellationToken);
        if (_runtime.HasPendingLoginRound)
        {
            var priority = _runtime.SelectReadyPriorityQueueItem(options);
            if (priority is not null)
                return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(priority)]);
            await _runtime.RunPendingLoginRoundAsync(options, cancellationToken);
            if (_runtime.HasPendingLoginRound)
                return new AutomationStateSnapshot([], NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(10));
        }

        var selected = _runtime.SelectNextQueueItem();
        if (selected is not null)
        {
            _runtime.PrioritizeDeadlineWorkOnWake = false;
            return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(selected)]);
        }

        var now = _timeProvider.GetUtcNow();
        var eligibleItems = _runtime.GetEligibleQueueItems();
        var nextDeferredItem = eligibleItems
            .Where(item => !item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
            .FirstOrDefault(item => item.NextAttemptAt > now)
            ?? eligibleItems
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefault();

        if (nextDeferredItem is null)
        {
            _port.Log($"[AUTOQ {_runtime.RunLogId}] DONE (queue empty).");
            return new AutomationStateSnapshot([], IsComplete: true);
        }

        _port.Log(
            $"[AUTOQ {_runtime.RunLogId}] WAIT "
            + $"{Math.Max(0, (nextDeferredItem.NextAttemptAt - now).TotalSeconds):F0}s "
            + $"for deferred task={nextDeferredItem.TaskName}");
        var smartSleepDelay = SmartSleepDeadlinePolicy.ResolveNextDelay(
            now,
            eligibleItems,
            _runtime.SmartSleepDeadlineGroups,
            nextConstructionAvailabilityUtc: null,
            queueDeadlineOverrides: _runtime.GetSmartSleepQueueDeadlineOverrides(eligibleItems, now));
        _ = _port.TryRequestSmartSleep(smartSleepDelay is { } delay ? now.Add(delay) : null);
        return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(nextDeferredItem)]);
    }

    public ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        CancellationToken cancellationToken) => _port.ExecuteAsync(action, cancellationToken);

    public ValueTask CompleteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        AutomationActionOutcome outcome,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
