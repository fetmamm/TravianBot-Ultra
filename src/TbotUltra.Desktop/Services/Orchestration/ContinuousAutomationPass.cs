using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed record ContinuousAutomationDeadlineSnapshot(
    DateTimeOffset? NextQueueDeadlineUtc,
    DateTimeOffset? NextConstructionAvailabilityUtc,
    DateTimeOffset? NextVillageStatusRoundUtc,
    IReadOnlyList<QueueItem> SmartSleepItems,
    IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups,
    DateTimeOffset? SmartSleepConstructionAvailabilityUtc = null,
    IReadOnlyDictionary<Guid, DateTimeOffset>? SmartSleepQueueDeadlineOverrides = null);

internal interface IContinuousAutomationPassPort
{
    BotOptions LoadOptions();
    bool TryScheduleAutomaticProxyRecovery(BotOptions options);
    DateTimeOffset VillageMembershipVerificationNotBeforeUtc { get; }
    ValueTask EnsureChromiumInstalledAsync();
    ValueTask<bool> EnsureVillageMembershipVerifiedAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask EnsureConstructionStatusAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask MaybeAnalyzeNewVillageAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask MaybeCheckInboxAsync(CancellationToken cancellationToken);
    void LogSmartSleepBlockedByReadyTask(QueueItem item);
    ValueTask MaybeKeepBrowserFreshAsync(BotOptions options, CancellationToken cancellationToken);
    bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc);
    void Log(string message);
    string FormatException(Exception exception);
    ValueTask HoldAccountAutomationAsync(AccountAccessException exception);
    ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationCandidate action,
        CancellationToken cancellationToken);
}

internal sealed class ContinuousAutomationPass : IAutomationModePassPort
{
    private static readonly TimeSpan IdleHeartbeatInterval = TimeSpan.FromMinutes(2);
    private readonly IContinuousAutomationPassPort _port;
    private readonly IContinuousAutomationPassRuntime _runtime;
    private readonly TimeProvider _timeProvider;

    internal ContinuousAutomationPass(
        IContinuousAutomationPassPort port,
        TimeProvider? timeProvider = null)
        : this(
            port,
            port as IContinuousAutomationPassRuntime
                ?? throw new ArgumentException("A runtime is required for the continuous pass.", nameof(port)),
            timeProvider)
    {
    }

    internal ContinuousAutomationPass(
        IContinuousAutomationPassPort port,
        IContinuousAutomationPassRuntime runtime,
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
        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(_port.LoadOptions());
        var passId = _runtime.BeginPass();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (_port.TryScheduleAutomaticProxyRecovery(options))
            {
                return new AutomationStateSnapshot([], IsComplete: true);
            }

            var networkBackoffRemaining = _runtime.NetworkBackoffRemaining;
            if (networkBackoffRemaining > TimeSpan.Zero)
            {
                _port.Log($"[LOOP {passId}] WAIT {Math.Ceiling(networkBackoffRemaining.TotalSeconds):F0}s");
                return new AutomationStateSnapshot(
                    [],
                    NextWakeAt: _timeProvider.GetUtcNow().Add(networkBackoffRemaining));
            }

            await _port.EnsureChromiumInstalledAsync();
            if (!await _port.EnsureVillageMembershipVerifiedAsync(options, cancellationToken))
            {
                var now = _timeProvider.GetUtcNow();
                return new AutomationStateSnapshot(
                    [],
                    NextWakeAt: _port.VillageMembershipVerificationNotBeforeUtc > now
                        ? _port.VillageMembershipVerificationNotBeforeUtc
                        : now.AddSeconds(30));
            }

            var immediateWorkRequested = _runtime.ConsumeImmediateWorkRequest();
            var loginRoundPending = _runtime.HasPendingLoginRound;
            if (!immediateWorkRequested && !loginRoundPending)
            {
                await _runtime.MaybeTakeIdleBreakAsync(options, cancellationToken);
                immediateWorkRequested = _runtime.ConsumeImmediateWorkRequest();
            }
            if (!immediateWorkRequested && !loginRoundPending)
            {
                await _runtime.MaybeDoIdleBrowseAsync(options, cancellationToken);
            }

            await _port.HonorPendingVillageSwitchAsync(options, cancellationToken);
            if (loginRoundPending)
            {
                await _runtime.EnsureRuntimeItemsAsync(options, cancellationToken);
                var priority = _runtime.SelectReadyPriorityQueueItem(options);
                if (priority is not null)
                {
                    _port.Log($"[village-round] explicit priority task runs before village round: {priority.TaskName}.");
                    _runtime.MarkActivePass();
                    return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(priority)]);
                }
                _runtime.PrioritizeDeadlineWorkOnWake = false;
            }
            var prioritizeDeadlineWork = _runtime.PrioritizeDeadlineWorkOnWake;
            if (!prioritizeDeadlineWork)
            {
                var forceVillageStatusRound = _runtime.ConsumeForceVillageStatusRoundRequest();
                await _runtime.MaybeRunVillageStatusRoundAsync(options, cancellationToken, forceVillageStatusRound);
                if (_runtime.HasPendingLoginRound)
                {
                    _port.Log("[village-round] round paused before completion; ordinary tasks remain deferred.");
                    return new AutomationStateSnapshot([], NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(10));
                }
            }
            else
            {
                _port.Log("[smart-sleep] deadline wake is checking queued work before Village scan.");
            }

            await _port.EnsureConstructionStatusAsync(options, cancellationToken);
            await _port.MaybeAnalyzeNewVillageAsync(options, cancellationToken);
            await _runtime.EnsureRuntimeItemsAsync(options, cancellationToken);
            await _port.MaybeCheckInboxAsync(cancellationToken);

            var next = _runtime.SelectNextQueueItem();
            if (next is not null)
            {
                _port.LogSmartSleepBlockedByReadyTask(next);
                _runtime.PrioritizeDeadlineWorkOnWake = false;
                LogSelection(passId, next);
                _runtime.MarkActivePass();
                return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(next)]);
            }

            if (prioritizeDeadlineWork)
            {
                _runtime.PrioritizeDeadlineWorkOnWake = false;
                var forceVillageStatusRound = _runtime.ConsumeForceVillageStatusRoundRequest();
                await _runtime.MaybeRunVillageStatusRoundAsync(options, cancellationToken, forceVillageStatusRound);
                if (_runtime.HasPendingLoginRound)
                    return new AutomationStateSnapshot([], NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(10));
                await _runtime.EnsureRuntimeItemsAsync(options, cancellationToken);
                next = _runtime.SelectNextQueueItem();
                if (next is not null)
                {
                    _port.LogSmartSleepBlockedByReadyTask(next);
                    LogSelection(passId, next);
                    _runtime.MarkActivePass();
                    return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(next)]);
                }
            }

            await _port.MaybeKeepBrowserFreshAsync(options, cancellationToken);
            var nowForDeadline = _timeProvider.GetUtcNow();
            ContinuousAutomationDeadlineSnapshot deadlines;
            try
            {
                deadlines = _runtime.ReadDeadlines(options);
            }
            catch (Exception ex)
            {
                _port.Log($"[smart-sleep] deadline calculation failed; using fallback check: {ex.Message}");
                deadlines = new ContinuousAutomationDeadlineSnapshot(
                    null,
                    null,
                    null,
                    [],
                    SmartSleepDeadlinePolicy.AllGroups.ToHashSet());
            }
            var waitDelay = AutomationDeadlinePolicy.ResolveNextDelay(
                nowForDeadline,
                deadlines.NextQueueDeadlineUtc,
                deadlines.NextConstructionAvailabilityUtc,
                deadlines.NextVillageStatusRoundUtc);
            var smartSleepDelay = SmartSleepDeadlinePolicy.ResolveNextDelay(
                nowForDeadline,
                deadlines.SmartSleepItems,
                deadlines.SmartSleepDeadlineGroups,
                deadlines.SmartSleepConstructionAvailabilityUtc,
                deadlines.SmartSleepQueueDeadlineOverrides);
            DateTimeOffset? smartSleepDeadline = smartSleepDelay is { } trustedDelay
                ? nowForDeadline.Add(trustedDelay)
                : null;
            var smartSleepRequested = _port.TryRequestSmartSleep(smartSleepDeadline);
            var totalSeconds = AutomationDeadlinePolicy.ResolveWaitSeconds(
                waitDelay,
                options,
                networkBackoff: false);
            if (_runtime.ShouldPublishIdleHeartbeat(IdleHeartbeatInterval))
            {
                _port.Log($"[LOOP {passId}] idle — nothing ready, waiting {totalSeconds}s");
            }

            var nextWakeAt = nowForDeadline.AddSeconds(totalSeconds);
            if (!smartSleepRequested
                && options.ContinuousKeepAliveEnabled
                && _runtime.NextKeepAliveAtUtc > nowForDeadline
                && _runtime.NextKeepAliveAtUtc < nextWakeAt)
            {
                nextWakeAt = _runtime.NextKeepAliveAtUtc;
            }

            return new AutomationStateSnapshot([], NextWakeAt: nextWakeAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountAccessException ex)
        {
            await _port.HoldAccountAutomationAsync(ex);
            throw;
        }
        catch (Exception ex) when (AutomationNetworkBackoff.IsTransientConnectionFailure(ex))
        {
            if (_port.TryScheduleAutomaticProxyRecovery(options))
            {
                return new AutomationStateSnapshot([], IsComplete: true);
            }

            throw;
        }
        catch (Exception ex)
        {
            _port.Log(
                $"[LOOP {passId}] FAIL {stopwatch.Elapsed.TotalSeconds:F1}s | "
                + _port.FormatException(ex));
            var retrySeconds = AutomationDeadlinePolicy.ResolveWaitSeconds(
                null,
                options,
                networkBackoff: false);
            return new AutomationStateSnapshot(
                [],
                NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(retrySeconds));
        }
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

    private void LogSelection(long passId, QueueItem item)
    {
        _port.Log(
            $"[LOOP {passId}] PICK group={item.Group}, task={item.TaskName}, "
            + $"retries={item.Retries}/{item.MaxRetries}");
    }
}
