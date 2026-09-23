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
    long BeginPass();
    bool TryScheduleAutomaticProxyRecovery(BotOptions options);
    TimeSpan NetworkBackoffRemaining { get; }
    DateTimeOffset VillageMembershipVerificationNotBeforeUtc { get; }
    DateTimeOffset NextKeepAliveAtUtc { get; }
    bool PrioritizeDeadlineWorkOnWake { get; set; }
    bool HasPendingLoginRound { get; }
    QueueItem? SelectReadyPriorityQueueItem(BotOptions options);
    ValueTask EnsureChromiumInstalledAsync();
    ValueTask<bool> EnsureVillageMembershipVerifiedAsync(BotOptions options, CancellationToken cancellationToken);
    bool ConsumeImmediateWorkRequest();
    ValueTask MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask MaybeDoIdleBrowseAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken cancellationToken);
    bool ConsumeForceVillageStatusRoundRequest();
    ValueTask MaybeRunVillageStatusRoundAsync(BotOptions options, CancellationToken cancellationToken, bool force);
    ValueTask EnsureConstructionStatusAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask MaybeAnalyzeNewVillageAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask EnsureRuntimeItemsAsync(BotOptions options, CancellationToken cancellationToken);
    ValueTask MaybeCheckInboxAsync(CancellationToken cancellationToken);
    QueueItem? SelectNextQueueItem();
    void LogSmartSleepBlockedByReadyTask(QueueItem item);
    void MarkActivePass();
    ValueTask MaybeKeepBrowserFreshAsync(BotOptions options, CancellationToken cancellationToken);
    ContinuousAutomationDeadlineSnapshot ReadDeadlines(BotOptions options);
    bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc);
    bool ShouldPublishIdleHeartbeat(TimeSpan interval);
    void Log(string message);
    string FormatException(Exception exception);
    ValueTask HoldAccountAutomationAsync(AccountAccessException exception);
    ValueTask<AutomationActionOutcome> ExecuteAsync(
        AutomationCandidate action,
        CancellationToken cancellationToken);
}

internal sealed class ContinuousAutomationPass(
    IContinuousAutomationPassPort port,
    TimeProvider? timeProvider = null) : IAutomationModePassPort
{
    private static readonly TimeSpan IdleHeartbeatInterval = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<AutomationStateSnapshot> ReadAsync(
        AutomationRunContext context,
        CancellationToken cancellationToken)
    {
        var options = AutomationExecutionOptions.WithoutImplicitVillageTarget(port.LoadOptions());
        var passId = port.BeginPass();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (port.TryScheduleAutomaticProxyRecovery(options))
            {
                return new AutomationStateSnapshot([], IsComplete: true);
            }

            var networkBackoffRemaining = port.NetworkBackoffRemaining;
            if (networkBackoffRemaining > TimeSpan.Zero)
            {
                port.Log($"[LOOP {passId}] WAIT {Math.Ceiling(networkBackoffRemaining.TotalSeconds):F0}s");
                return new AutomationStateSnapshot(
                    [],
                    NextWakeAt: _timeProvider.GetUtcNow().Add(networkBackoffRemaining));
            }

            await port.EnsureChromiumInstalledAsync();
            if (!await port.EnsureVillageMembershipVerifiedAsync(options, cancellationToken))
            {
                var now = _timeProvider.GetUtcNow();
                return new AutomationStateSnapshot(
                    [],
                    NextWakeAt: port.VillageMembershipVerificationNotBeforeUtc > now
                        ? port.VillageMembershipVerificationNotBeforeUtc
                        : now.AddSeconds(30));
            }

            var immediateWorkRequested = port.ConsumeImmediateWorkRequest();
            var loginRoundPending = port.HasPendingLoginRound;
            if (!immediateWorkRequested && !loginRoundPending)
            {
                await port.MaybeTakeIdleBreakAsync(options, cancellationToken);
                immediateWorkRequested = port.ConsumeImmediateWorkRequest();
            }
            if (!immediateWorkRequested && !loginRoundPending)
            {
                await port.MaybeDoIdleBrowseAsync(options, cancellationToken);
            }

            await port.HonorPendingVillageSwitchAsync(options, cancellationToken);
            if (loginRoundPending)
            {
                await port.EnsureRuntimeItemsAsync(options, cancellationToken);
                var priority = port.SelectReadyPriorityQueueItem(options);
                if (priority is not null)
                {
                    port.Log($"[village-round] explicit priority task runs before village round: {priority.TaskName}.");
                    port.MarkActivePass();
                    return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(priority)]);
                }
                port.PrioritizeDeadlineWorkOnWake = false;
            }
            var prioritizeDeadlineWork = port.PrioritizeDeadlineWorkOnWake;
            if (!prioritizeDeadlineWork)
            {
                var forceVillageStatusRound = port.ConsumeForceVillageStatusRoundRequest();
                await port.MaybeRunVillageStatusRoundAsync(options, cancellationToken, forceVillageStatusRound);
                if (port.HasPendingLoginRound)
                {
                    port.Log("[village-round] round paused before completion; ordinary tasks remain deferred.");
                    return new AutomationStateSnapshot([], NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(10));
                }
            }
            else
            {
                port.Log("[smart-sleep] deadline wake is checking queued work before Village scan.");
            }

            await port.EnsureConstructionStatusAsync(options, cancellationToken);
            await port.MaybeAnalyzeNewVillageAsync(options, cancellationToken);
            await port.EnsureRuntimeItemsAsync(options, cancellationToken);
            await port.MaybeCheckInboxAsync(cancellationToken);

            var next = port.SelectNextQueueItem();
            if (next is not null)
            {
                port.LogSmartSleepBlockedByReadyTask(next);
                port.PrioritizeDeadlineWorkOnWake = false;
                LogSelection(passId, next);
                port.MarkActivePass();
                return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(next)]);
            }

            if (prioritizeDeadlineWork)
            {
                port.PrioritizeDeadlineWorkOnWake = false;
                var forceVillageStatusRound = port.ConsumeForceVillageStatusRoundRequest();
                await port.MaybeRunVillageStatusRoundAsync(options, cancellationToken, forceVillageStatusRound);
                if (port.HasPendingLoginRound)
                    return new AutomationStateSnapshot([], NextWakeAt: _timeProvider.GetUtcNow().AddSeconds(10));
                await port.EnsureRuntimeItemsAsync(options, cancellationToken);
                next = port.SelectNextQueueItem();
                if (next is not null)
                {
                    port.LogSmartSleepBlockedByReadyTask(next);
                    LogSelection(passId, next);
                    port.MarkActivePass();
                    return new AutomationStateSnapshot([AutomationCandidate.FromQueueItem(next)]);
                }
            }

            await port.MaybeKeepBrowserFreshAsync(options, cancellationToken);
            var nowForDeadline = _timeProvider.GetUtcNow();
            ContinuousAutomationDeadlineSnapshot deadlines;
            try
            {
                deadlines = port.ReadDeadlines(options);
            }
            catch (Exception ex)
            {
                port.Log($"[smart-sleep] deadline calculation failed; using fallback check: {ex.Message}");
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
            var smartSleepRequested = port.TryRequestSmartSleep(smartSleepDeadline);
            var totalSeconds = AutomationDeadlinePolicy.ResolveWaitSeconds(
                waitDelay,
                options,
                networkBackoff: false);
            if (port.ShouldPublishIdleHeartbeat(IdleHeartbeatInterval))
            {
                port.Log($"[LOOP {passId}] idle — nothing ready, waiting {totalSeconds}s");
            }

            var nextWakeAt = nowForDeadline.AddSeconds(totalSeconds);
            if (!smartSleepRequested
                && options.ContinuousKeepAliveEnabled
                && port.NextKeepAliveAtUtc > nowForDeadline
                && port.NextKeepAliveAtUtc < nextWakeAt)
            {
                nextWakeAt = port.NextKeepAliveAtUtc;
            }

            return new AutomationStateSnapshot([], NextWakeAt: nextWakeAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountAccessException ex)
        {
            await port.HoldAccountAutomationAsync(ex);
            throw;
        }
        catch (Exception ex) when (AutomationNetworkBackoff.IsTransientConnectionFailure(ex))
        {
            if (port.TryScheduleAutomaticProxyRecovery(options))
            {
                return new AutomationStateSnapshot([], IsComplete: true);
            }

            throw;
        }
        catch (Exception ex)
        {
            port.Log(
                $"[LOOP {passId}] FAIL {stopwatch.Elapsed.TotalSeconds:F1}s | "
                + port.FormatException(ex));
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
        CancellationToken cancellationToken) => port.ExecuteAsync(action, cancellationToken);

    public ValueTask CompleteAsync(
        AutomationRunContext context,
        AutomationCandidate action,
        AutomationActionOutcome outcome,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private void LogSelection(long passId, QueueItem item)
    {
        port.Log(
            $"[LOOP {passId}] PICK group={item.Group}, task={item.TaskName}, "
            + $"retries={item.Retries}/{item.MaxRetries}");
    }
}
