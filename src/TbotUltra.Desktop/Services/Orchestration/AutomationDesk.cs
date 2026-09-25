using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

/// <summary>
/// Owns the authoritative Desktop automation run state and the run-mode modules
/// behind the caller-first interface.
/// </summary>
public sealed class AutomationDesk : IAutomationDesk, IAsyncDisposable
{
    private readonly LoopController _loopController;
    private readonly IAutomationModePassPort _continuousLoop;
    private readonly IAutomationModePassPort _autoQueue;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly AutomationNetworkBackoff _networkBackoff;
    private readonly AutomationRuntime? _runtime;
    private readonly object _sync = new();
    private readonly object _publicationSync = new();
    private readonly SortedDictionary<long, AutomationUpdate> _pendingPublications = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly AutomationMailbox _mailbox = new();
    private long _nextRunId;
    private long _nextUpdateSequence;
    private long _nextPublicationSequence = 1;
    private Task? _runTask;
    private LoopController.GateLease? _autoQueueGateLease;
    private TaskCompletionSource<AutomationDecisionChoice?>? _pendingDecisionCompletion;
    private AutomationDecisionRequestId? _lastAnsweredDecisionId;
    private readonly HashSet<AutomationDecisionRequestId> _staleDecisionIds = [];
    private AutomationSnapshot _current = AutomationSnapshot.Stopped;

    public AutomationDesk(LoopController loopController, TimeProvider? timeProvider = null)
        : this(
            loopController,
            EmptyAutomationModePass.Instance,
            EmptyAutomationModePass.Instance,
            timeProvider)
    {
    }

    internal AutomationDesk(
        LoopController loopController,
        IAutomationModePassPort continuousLoop,
        IAutomationModePassPort autoQueue,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        AutomationRuntime? runtime = null)
    {
        _loopController = loopController;
        _continuousLoop = continuousLoop;
        _autoQueue = autoQueue;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) =>
            Task.Delay(delay, _timeProvider, cancellationToken));
        _networkBackoff = runtime?.NetworkBackoff ?? new AutomationNetworkBackoff(_timeProvider);
        _runtime = runtime;
        _loopController.AutomationStopRequested += OnAutomationStopRequested;
    }

    private AutomationRuntime Runtime => _runtime
        ?? throw new InvalidOperationException("Automation runtime is not configured for this desk.");

    internal long BeginContinuousPass() => Runtime.Pass.BeginContinuousPass();

    internal long CurrentContinuousPassId => Runtime.Pass.CurrentContinuousPassId;

    internal long AutoQueueRunLogId => Runtime.Pass.AutoQueueRunLogId;

    internal void BeginAutoQueueRun(long logId) => Runtime.Pass.BeginAutoQueueRun(logId);

    internal void RequestImmediateWork() => Runtime.Pass.RequestImmediateWork();

    internal bool ConsumeImmediateWorkRequest() => Runtime.Pass.ConsumeImmediateWorkRequest();

    internal bool IsImmediateWorkRequested => Runtime.Pass.IsImmediateWorkRequested;

    internal bool PrioritizeDeadlineWorkOnWake
    {
        get => Runtime.Pass.PrioritizeDeadlineWorkOnWake;
        set => Runtime.Pass.PrioritizeDeadlineWorkOnWake = value;
    }

    internal IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups => Runtime.Pass.SmartSleepDeadlineGroups;

    internal void SetSmartSleepDeadlineGroups(IReadOnlySet<QueueGroup> groups) =>
        Runtime.Pass.SetSmartSleepDeadlineGroups(groups);

    internal VillageBatchSnapshot SnapshotVillageBatch(string? verifiedVillageKey) =>
        Runtime.Pass.SnapshotVillageBatch(verifiedVillageKey);

    internal void ObserveVerifiedVillage(string? villageKey) => Runtime.Pass.ObserveVerifiedVillage(villageKey);

    internal VillageBatchSnapshot RecordVillageAttempt(
        string? targetVillageKey,
        string? verifiedVillageKey) =>
        Runtime.Pass.RecordVillageAttempt(targetVillageKey, verifiedVillageKey);

    internal void RecordUrgentPreemption(string? currentVillageKey, string? targetVillageKey) =>
        Runtime.Pass.RecordUrgentPreemption(currentVillageKey, targetVillageKey);

    internal void CompleteUrgentPreemption(string? verifiedVillageKey) =>
        Runtime.Pass.CompleteUrgentPreemption(verifiedVillageKey);

    internal void ResetVillageBatch() => Runtime.Pass.ResetVillageBatch();

    internal void ResetIdlePacing() => Runtime.IdlePacingState.Reset();

    internal int ConsecutiveNetworkFailures => Runtime.NetworkBackoff.ConsecutiveFailures;

    internal bool IsNetworkUnavailable => Runtime.NetworkBackoff.IsUnavailable;

    internal TimeSpan NetworkBackoffRemaining => Runtime.NetworkBackoff.Remaining;

    internal TimeSpan NextNetworkRetryDelay() => Runtime.NetworkBackoff.NextRetryDelay();

    internal void MarkNetworkUnavailable(TimeSpan delay) => Runtime.NetworkBackoff.MarkUnavailable(delay);

    internal void MarkNetworkHealthy() => Runtime.NetworkBackoff.MarkHealthy();

    internal bool TryReserveProxyRecovery(int consecutiveFailures, int failureThreshold) =>
        Runtime.ProxyRecovery.TryReserve(consecutiveFailures, failureThreshold);

    internal void ReleaseProxyRecovery() => Runtime.ProxyRecovery.Release();

    internal AutomationProxyRecoveryRetry ScheduleProxyRecoveryRetry() => Runtime.ProxyRecovery.ScheduleRetry();

    internal void ResetProxyRecoveryRetry() => Runtime.ProxyRecovery.ResetRetry();

    internal DateTimeOffset NextKeepAliveAtUtc => Runtime.Session.NextKeepAliveAtUtc;

    internal bool ShouldCheckInbox(bool enabled, TimeSpan interval) => Runtime.Session.ShouldCheckInbox(enabled, interval);

    internal void RecordBrowserActivity(bool enabled, int minMinutes, int maxMinutes) =>
        Runtime.Session.RecordBrowserActivity(enabled, minMinutes, maxMinutes);

    internal KeepAlivePlan PlanKeepAlive(
        bool enabled,
        int minMinutes,
        int maxMinutes,
        bool sessionSleeping,
        bool refreshRunning,
        bool workDueSoon,
        DateTimeOffset? nextPendingAt) =>
        Runtime.Session.PlanKeepAlive(
            enabled,
            minMinutes,
            maxMinutes,
            sessionSleeping,
            refreshRunning,
            workDueSoon,
            nextPendingAt);

    internal void MarkKeepAliveFailure() => Runtime.Session.MarkKeepAliveFailure();

    internal GoldClubCheckPlan PlanGoldClubCheck(
        string? accountName,
        bool? storedEnabled,
        TimeSpan inactiveRecheckInterval) =>
        Runtime.Session.PlanGoldClubCheck(accountName, storedEnabled, inactiveRecheckInterval);

    internal bool ApplyGoldClubStatus(bool enabled) => Runtime.Session.ApplyGoldClubStatus(enabled);

    internal void RequestConstructionStatusSync() => Runtime.Session.RequestConstructionStatusSync();

    internal bool ConstructionStatusNeedsSync => Runtime.Session.ConstructionStatusNeedsSync;

    internal void MarkConstructionStatusSynchronized() => Runtime.Session.MarkConstructionStatusSynchronized();

    internal bool ShouldPublishWarnings(string signature) => Runtime.Session.ShouldPublishWarnings(signature);

    internal bool ShouldPublishIdleHeartbeat(TimeSpan interval) => Runtime.Session.ShouldPublishIdleHeartbeat(interval);

    internal void MarkActivePass() => Runtime.Session.MarkActivePass();

    internal bool ShouldPublishVerbose(string key, TimeSpan interval) =>
        Runtime.Session.ShouldPublishVerbose(key, interval);

    internal bool TrySetConstructionSummary(string villageKey, string state) =>
        Runtime.Session.TrySetConstructionSummary(villageKey, state);

    internal void ClearConstructionSummary(string villageKey) => Runtime.Session.ClearConstructionSummary(villageKey);

    internal void ResetSessionRuntime() => Runtime.Session.Reset();

    internal bool IsQueueItemAllowed(QueueItem item) => Runtime.QueueEligibility.IsAllowed(item);

    internal bool IsGroupEnabled(string? villageKey, QueueGroup group) =>
        Runtime.QueueEligibility.IsGroupEnabled(villageKey, group);

    internal QueueItem? SelectQueueItem(
        bool preview = false,
        DateTimeOffset? evaluationTimeUtc = null,
        string? villageKeyFilter = null,
        IReadOnlyList<QueueItem>? queueItemsOverride = null) =>
        Runtime.QueueSelection.Select(preview, evaluationTimeUtc, villageKeyFilter, queueItemsOverride);

    internal QueueItem? SelectReadyPriorityQueueItem(BotOptions options) =>
        Runtime.QueueSelection.SelectUrgent(options, new HashSet<Guid>(), explicitPriorityOnly: true);

    internal ContinuousLoopForecast ResolveForecast(
        DateTimeOffset now,
        string? villageKeyFilter = null,
        IReadOnlyList<QueueItem>? queueItemsOverride = null,
        bool wakeWhenConstructionQueueClears = false) =>
        Runtime.Forecast.Resolve(now, villageKeyFilter, queueItemsOverride, wakeWhenConstructionQueueClears);

    internal ContinuousAutomationDeadlineSnapshot ReadDeadlines(BotOptions options) =>
        Runtime.Deadlines.Read(options);

    internal ValueTask PrepareRuntimeItemsAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        AutomationRuntimeVillage? onlyVillage = null) =>
        Runtime.RuntimeItemPreparation.PrepareAsync(options, cancellationToken, onlyVillage);

    internal ValueTask MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken cancellationToken) =>
        Runtime.IdlePacing.MaybeTakeBreakAsync(options, cancellationToken);

    internal ValueTask MaybeBrowseAsync(BotOptions options, CancellationToken cancellationToken) =>
        Runtime.IdlePacing.MaybeBrowseAsync(options, cancellationToken);

    internal DateTimeOffset GetNextVillageStatusRoundUtc(string? accountName) =>
        Runtime.VillageStatusRoundState.GetNextRoundUtc(accountName);

    internal bool ResetVillageStatusRound(string? accountName) => Runtime.VillageStatusRoundState.Reset(accountName);

    internal VillageStatusRoundScheduleResult ScheduleNextVillageStatusRound(
        string? expectedAccountName,
        string? currentAccountName,
        int minMinutes,
        int maxMinutes) =>
        Runtime.VillageStatusRoundState.ScheduleNext(
            expectedAccountName,
            currentAccountName,
            minMinutes,
            maxMinutes);

    internal void RequestForcedVillageStatusRound() => Runtime.VillageStatusRoundState.RequestForce();

    internal bool ConsumeForcedVillageStatusRoundRequest() => Runtime.VillageStatusRoundState.ConsumeForceRequest();

    internal void SetForceVillageStatusRoundOnWake(bool requested) =>
        Runtime.VillageStatusRoundState.SetForceOnWakeRequest(requested);

    internal bool ConsumeForceVillageStatusRoundOnWake() => Runtime.VillageStatusRoundState.ConsumeForceOnWakeRequest();

    internal bool TryBeginManualVillageStatusRound() => Runtime.VillageStatusRoundState.TryBeginManualRun();

    internal void EndManualVillageStatusRound() => Runtime.VillageStatusRoundState.EndManualRun();

    internal bool LoginVillageStatusRoundPending => Runtime.VillageStatusRound.LoginRoundPending;

    internal void RequestLoginVillageStatusRound(bool preserveIncomplete = false) =>
        Runtime.VillageStatusRound.RequestLoginRound(preserveIncomplete);

    internal void ResetLoginVillageStatusRound() => Runtime.VillageStatusRound.ResetLoginRound();

    internal ValueTask RunVillageStatusRoundAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        bool force = false) =>
        Runtime.VillageStatusRound.RunIfDueAsync(options, cancellationToken, force);

    internal ValueTask<bool> ExecuteVillageStatusTasksAsync(
        BotOptions options,
        AutomationRuntimeVillage village,
        CancellationToken cancellationToken) =>
        Runtime.VillageStatusTasks.ExecuteAsync(options, village, cancellationToken);

    internal ValueTask<bool> ExecuteQueueItemAsync(
        QueueItem item,
        BotOptions options,
        string logPrefix,
        AutomationRunMode mode,
        CancellationToken cancellationToken) =>
        Runtime.QueueItemLifecycle.ExecuteAsync(item, options, logPrefix, mode, cancellationToken);

    public AutomationSnapshot Current
    {
        get => Volatile.Read(ref _current);
        private set => Volatile.Write(ref _current, value);
    }

    public event AutomationUpdatedEventHandler? Updated;

    public async ValueTask<AutomationStartResult> StartAsync(
        AutomationStart request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Context.AccountKey);
        ArgumentNullException.ThrowIfNull(request.Context.OfficialServerRoot);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AutomationRunId? previousRunId;
            lock (_sync)
            {
                if (Current.Phase == AutomationPhase.Running
                    && Current.Mode == request.Mode
                    && Current.Context == request.Context)
                {
                    return new AutomationStartResult.AlreadyRunning(Current.RunId!.Value);
                }

                previousRunId = Current.RunId;
            }

            if (previousRunId is not null)
            {
                await StopCurrentRunAsync(
                        AutomationStopMode.CancelCurrentAction,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            LoopController.GateLease? acquiredGate = null;
            if (request.Mode == AutomationRunMode.AutoQueue)
            {
                acquiredGate = await _loopController
                    .TryAcquireQueueAutoRunGateAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (acquiredGate is null)
                {
                    return new AutomationStartResult.Busy();
                }
            }

            AutomationUpdate update;
            AutomationRunId runId;
            TaskCompletionSource runStarted;
            lock (_sync)
            {
                var runToken = StartLifecycle(request.Mode);
                _autoQueueGateLease = acquiredGate;
                _mailbox.Reset();
                runId = new AutomationRunId(Interlocked.Increment(ref _nextRunId));
                Current = new AutomationSnapshot(runId, request.Mode, request.Context, AutomationPhase.Running);
                _networkBackoff.MarkHealthy();
                var automationEvent = new AutomationEvent.RunStarted(
                    runId,
                    _timeProvider.GetUtcNow(),
                    request.Mode,
                    request.Context);
                update = new AutomationUpdate(
                    Interlocked.Increment(ref _nextUpdateSequence),
                    automationEvent,
                    Current);
                runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _runTask = Task.Run(
                    async () =>
                    {
                        await runStarted.Task.ConfigureAwait(false);
                        await RunAutomationAsync(runId, request.Mode, request.Context, runToken)
                            .ConfigureAwait(false);
                    },
                    runToken);
            }

            PublishUpdate(update);
            runStarted.TrySetResult();
            return previousRunId is AutomationRunId previous
                ? new AutomationStartResult.Replaced(previous, runId)
                : new AutomationStartResult.Started(runId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<AutomationStopResult> StopAsync(
        AutomationStopMode mode = AutomationStopMode.AfterCurrentAction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == AutomationStopMode.CancelCurrentAction)
        {
            lock (_sync)
            {
                if (Current.Phase == AutomationPhase.Stopping)
                {
                    CancelCurrentRun(cancelCurrentAction: true);
                    _mailbox.Signal();
                }
            }
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StopCurrentRunAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async ValueTask<AutomationStopResult> StopCurrentRunAsync(
        AutomationStopMode mode,
        CancellationToken cancellationToken)
    {
        AutomationRunId runId;
        Task? runTask;
        lock (_sync)
        {
            if (Current.RunId is not AutomationRunId activeRunId)
            {
                return new AutomationStopResult.AlreadyStopped();
            }

            runId = activeRunId;
            Current = Current with { Phase = AutomationPhase.Stopping };
            if (Current.PendingDecision is { } staleDecision)
            {
                _staleDecisionIds.Add(staleDecision.RequestId);
            }
            _pendingDecisionCompletion?.TrySetResult(null);
            _pendingDecisionCompletion = null;
            CancelCurrentRun(mode == AutomationStopMode.CancelCurrentAction);
            _mailbox.Signal();
            _mailbox.ConsumeWake();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // CancelCurrentAction intentionally cancels the owned run token.
            }
        }

        AutomationUpdate? update = null;
        lock (_sync)
        {
            if (Current.RunId != runId)
            {
                return new AutomationStopResult.Stopped(runId);
            }

            var stoppedMode = Current.Mode;
            Current = AutomationSnapshot.Stopped;
            var automationEvent = new AutomationEvent.RunStopped(
                runId,
                _timeProvider.GetUtcNow(),
                stoppedMode!.Value,
                mode);
            update = new AutomationUpdate(
                Interlocked.Increment(ref _nextUpdateSequence),
                automationEvent,
                Current);
            _runTask = null;
            if (stoppedMode == AutomationRunMode.ContinuousLoop)
            {
                _loopController.DisposeLoop();
            }
            ReleaseAutoQueueGate();
        }

        PublishUpdate(update);
        return new AutomationStopResult.Stopped(runId);
    }

    public AutomationWakeResult Wake(AutomationWakeReason reason)
    {
        AutomationUpdate update;
        lock (_sync)
        {
            if (Current.Phase == AutomationPhase.Stopping)
            {
                return AutomationWakeResult.Stopping;
            }

            if (Current.Phase != AutomationPhase.Running)
            {
                return AutomationWakeResult.NotRunning;
            }

            if (!_mailbox.PostWake())
            {
                return AutomationWakeResult.Coalesced;
            }

            var automationEvent = new AutomationEvent.WakeAccepted(
                Current.RunId!.Value,
                _timeProvider.GetUtcNow(),
                reason);
            update = new AutomationUpdate(
                Interlocked.Increment(ref _nextUpdateSequence),
                automationEvent,
                Current);
        }

        PublishUpdate(update);
        return AutomationWakeResult.Accepted;
    }

    public AutomationDecisionResult Respond(
        AutomationDecisionRequestId requestId,
        AutomationDecisionChoice choice)
    {
        if (!Enum.IsDefined(choice))
        {
            return AutomationDecisionResult.InvalidChoice;
        }

        AutomationUpdate update;
        TaskCompletionSource<AutomationDecisionChoice?> completion;
        lock (_sync)
        {
            var pending = Current.PendingDecision;
            if (pending is null || _pendingDecisionCompletion is null)
            {
                if (_lastAnsweredDecisionId == requestId)
                {
                    return AutomationDecisionResult.AlreadyAnswered;
                }

                return _staleDecisionIds.Contains(requestId)
                    ? AutomationDecisionResult.StaleRun
                    : AutomationDecisionResult.UnknownRequest;
            }

            if (pending.RequestId != requestId)
            {
                return _staleDecisionIds.Contains(requestId)
                    ? AutomationDecisionResult.StaleRun
                    : AutomationDecisionResult.UnknownRequest;
            }

            completion = _pendingDecisionCompletion;
            _pendingDecisionCompletion = null;
            _lastAnsweredDecisionId = requestId;
            Current = Current with { PendingDecision = null };
            update = new AutomationUpdate(
                Interlocked.Increment(ref _nextUpdateSequence),
                new AutomationEvent.DecisionAnswered(
                    Current.RunId!.Value,
                    _timeProvider.GetUtcNow(),
                    pending,
                    choice),
                Current);
        }

        PublishUpdate(update);
        completion.TrySetResult(choice);
        return AutomationDecisionResult.Accepted;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(AutomationStopMode.CancelCurrentAction).ConfigureAwait(false);
        _loopController.AutomationStopRequested -= OnAutomationStopRequested;
        _mailbox.Dispose();
    }

    private void PublishUpdate(AutomationUpdate update)
    {
        lock (_publicationSync)
        {
            _pendingPublications[update.Sequence] = update;
            while (_pendingPublications.Remove(_nextPublicationSequence, out var next))
            {
                _nextPublicationSequence++;
                PublishToSubscribers(next);
            }
        }
    }

    private void PublishToSubscribers(AutomationUpdate update)
    {
        var subscribers = Updated;
        if (subscribers is null)
        {
            return;
        }

        foreach (AutomationUpdatedEventHandler subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, update);
            }
            catch (Exception)
            {
                // Presentation failures cannot change authoritative automation state.
            }
        }
    }

    private async Task RunAutomationAsync(
        AutomationRunId runId,
        AutomationRunMode mode,
        AutomationRunContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            while (IsCurrentRun(runId, context) && !ShouldStop())
            {
                var pass = await RunAutomationPassAsync(runId, mode, context, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsCurrentRun(runId, context) || ShouldStop())
                {
                    return;
                }

                if (pass.ExecutedAction)
                {
                    continue;
                }

                if (pass.CompletedRun)
                {
                    CompleteRun(runId, mode);
                    return;
                }

                await WaitForWakeOrDeadlineAsync(pass.NextDeadline, cancellationToken).ConfigureAwait(false);
                _mailbox.ConsumeWake();
            }
        }
        finally
        {
            if (ShouldStop())
            {
                CompleteRun(runId, mode);
            }
        }
    }

    private async Task<AutomationPassResult> RunAutomationPassAsync(
        AutomationRunId runId,
        AutomationRunMode mode,
        AutomationRunContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var modePass = ResolveModePass(mode);
            var snapshot = await modePass.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            _networkBackoff.MarkHealthy();
            var now = _timeProvider.GetUtcNow();
            var action = snapshot.Candidates
                .Where(candidate => candidate.NextAttemptAt <= now)
                .OrderByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.NextAttemptAt)
                .FirstOrDefault();
            if (action is null || !IsCurrentRun(runId, context))
            {
                if (snapshot.IsComplete)
                {
                    return AutomationPassResult.Complete;
                }

                var candidateDeadline = snapshot.Candidates
                    .Where(candidate => candidate.NextAttemptAt > now)
                    .Select(candidate => (DateTimeOffset?)candidate.NextAttemptAt)
                    .Min();
                var nextDeadline = snapshot.NextWakeAt is DateTimeOffset explicitWake
                    && (candidateDeadline is null || explicitWake < candidateDeadline.Value)
                        ? explicitWake
                        : candidateDeadline;
                return new AutomationPassResult(false, nextDeadline);
            }

            PublishRunEvent(new AutomationEvent.ActionSelected(
                runId,
                _timeProvider.GetUtcNow(),
                new QueueItemIdentity(action.Id),
                action.TaskName));
            if (action.Decision is AutomationDecisionRequirement requirement)
            {
                var choice = await RequestDecisionAsync(runId, requirement, cancellationToken)
                    .ConfigureAwait(false);
                if (choice is null || !IsCurrentRun(runId, context))
                {
                    return AutomationPassResult.NoWork;
                }

                if (choice == AutomationDecisionChoice.Decline)
                {
                    await modePass.CompleteAsync(
                            context,
                            action,
                            AutomationActionOutcome.Skipped,
                            cancellationToken)
                        .ConfigureAwait(false);
                    PublishRunEvent(new AutomationEvent.ActionFinished(
                        runId,
                        _timeProvider.GetUtcNow(),
                        new QueueItemIdentity(action.Id),
                        AutomationActionOutcome.Skipped));
                    return new AutomationPassResult(true, null);
                }
            }

            var outcome = await modePass
                .ExecuteAsync(context, action, cancellationToken)
                .ConfigureAwait(false);
            if (!IsCurrentRun(runId, context))
            {
                return AutomationPassResult.NoWork;
            }

            await modePass.CompleteAsync(context, action, outcome, cancellationToken)
                .ConfigureAwait(false);
            PublishRunEvent(new AutomationEvent.ActionFinished(
                runId,
                _timeProvider.GetUtcNow(),
                new QueueItemIdentity(action.Id),
                outcome));
            return outcome == AutomationActionOutcome.Blocked
                ? AutomationPassResult.Complete
                : new AutomationPassResult(true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is expected lifecycle control.
            return AutomationPassResult.NoWork;
        }
        catch (Exception ex)
        {
            var failure = AutomationFailureClassifier.Classify(ex);
            if (failure.IsRetryable && IsCurrentRun(runId, context))
            {
                var retryDelay = _networkBackoff.NextRetryDelay();
                _networkBackoff.MarkUnavailable(retryDelay);
                var retryAt = _timeProvider.GetUtcNow().Add(retryDelay);
                PublishRunEvent(new AutomationEvent.RunDeferred(
                    runId,
                    _timeProvider.GetUtcNow(),
                    mode,
                    failure,
                    retryAt));
                return new AutomationPassResult(false, retryAt);
            }

            AutomationUpdate? faultUpdate = null;
            lock (_sync)
            {
                if (Current.RunId == runId)
                {
                    Current = Current with { Phase = AutomationPhase.Faulted };
                    faultUpdate = new AutomationUpdate(
                        Interlocked.Increment(ref _nextUpdateSequence),
                        new AutomationEvent.RunFaulted(
                            runId,
                            _timeProvider.GetUtcNow(),
                            mode,
                            failure),
                        Current);
                    if (mode == AutomationRunMode.AutoQueue)
                    {
                        _loopController.DisposeAutoQueueRun();
                        ReleaseAutoQueueGate();
                    }
                    else
                    {
                        _loopController.DisposeLoop();
                    }
                }
            }

            if (faultUpdate is not null)
            {
                PublishUpdate(faultUpdate);
            }
            return AutomationPassResult.NoWork;
        }
    }

    private IAutomationModePassPort ResolveModePass(AutomationRunMode mode) => mode switch
    {
        AutomationRunMode.ContinuousLoop => _continuousLoop,
        AutomationRunMode.AutoQueue => _autoQueue,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown automation run mode."),
    };

    private void CompleteRun(AutomationRunId runId, AutomationRunMode mode)
    {
        AutomationUpdate? update = null;
        lock (_sync)
        {
            if (Current.RunId != runId || Current.Phase != AutomationPhase.Running)
            {
                return;
            }

            Current = AutomationSnapshot.Stopped;
            update = new AutomationUpdate(
                Interlocked.Increment(ref _nextUpdateSequence),
                new AutomationEvent.RunStopped(
                    runId,
                    _timeProvider.GetUtcNow(),
                    mode,
                    AutomationStopMode.AfterCurrentAction),
                Current);
            _runTask = null;
            if (mode == AutomationRunMode.AutoQueue)
            {
                _loopController.DisposeAutoQueueRun();
            }
            else
            {
                _loopController.DisposeLoop();
            }
            ReleaseAutoQueueGate();
        }

        PublishUpdate(update);
    }

    private async Task<AutomationDecisionChoice?> RequestDecisionAsync(
        AutomationRunId runId,
        AutomationDecisionRequirement requirement,
        CancellationToken cancellationToken)
    {
        AutomationUpdate update;
        TaskCompletionSource<AutomationDecisionChoice?> completion;
        lock (_sync)
        {
            if (Current.RunId != runId || Current.Phase != AutomationPhase.Running)
            {
                return null;
            }

            var decision = new AutomationPendingDecision(
                new AutomationDecisionRequestId(Guid.NewGuid()),
                requirement.Code);
            completion = new TaskCompletionSource<AutomationDecisionChoice?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDecisionCompletion = completion;
            Current = Current with { PendingDecision = decision };
            update = new AutomationUpdate(
                Interlocked.Increment(ref _nextUpdateSequence),
                new AutomationEvent.DecisionRequested(
                    runId,
                    _timeProvider.GetUtcNow(),
                    decision),
                Current);
        }

        PublishUpdate(update);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForWakeOrDeadlineAsync(
        DateTimeOffset? nextDeadline,
        CancellationToken cancellationToken)
    {
        if (nextDeadline is null)
        {
            await _mailbox.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var remaining = nextDeadline.Value - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wakeTask = _mailbox.WaitAsync(Timeout.InfiniteTimeSpan, waitCancellation.Token);
        var deadlineTask = _delayAsync(remaining, waitCancellation.Token);
        await Task.WhenAny(wakeTask, deadlineTask).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(wakeTask, deadlineTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The losing wait is canceled after the first wake source wins.
        }
    }

    private bool ShouldStop() =>
        Current.Mode == AutomationRunMode.ContinuousLoop
            ? _loopController.LoopStopRequested
            : _loopController.QueueStopRequested;

    private void OnAutomationStopRequested() => _mailbox.Signal();

    private bool IsCurrentRun(AutomationRunId runId, AutomationRunContext context)
    {
        lock (_sync)
        {
            return Current.RunId == runId
                && Current.Context == context
                && Current.Phase == AutomationPhase.Running;
        }
    }

    private void PublishRunEvent(AutomationEvent automationEvent)
    {
        AutomationUpdate? update;
        lock (_sync)
        {
            if (Current.RunId != automationEvent.RunId || Current.Phase != AutomationPhase.Running)
            {
                return;
            }

            update = new AutomationUpdate(
                Interlocked.Increment(ref _nextUpdateSequence),
                automationEvent,
                Current);
        }

        PublishUpdate(update);
    }

    private CancellationToken StartLifecycle(AutomationRunMode mode)
    {
        if (mode == AutomationRunMode.ContinuousLoop)
        {
            _loopController.ClearLoopStopRequest();
            return _loopController.StartLoop("automation-desk");
        }

        _loopController.ClearQueueStopRequest();
        return _loopController.StartAutoQueueRun();
    }

    private void ReleaseAutoQueueGate()
    {
        _autoQueueGateLease?.Dispose();
        _autoQueueGateLease = null;
    }

    private void CancelCurrentRun(bool cancelCurrentAction = true)
    {
        if (Current.Mode == AutomationRunMode.ContinuousLoop)
        {
            _loopController.RequestLoopStop();
            if (cancelCurrentAction)
            {
                _loopController.CancelLoop();
            }
            return;
        }

        if (Current.Mode == AutomationRunMode.AutoQueue)
        {
            _loopController.RequestQueueStop();
            if (cancelCurrentAction)
            {
                _loopController.CancelAutoQueueRun();
            }
            _loopController.DisposeAutoQueueRun();
        }
    }

    private sealed record AutomationPassResult(
        bool ExecutedAction,
        DateTimeOffset? NextDeadline,
        bool CompletedRun = false)
    {
        internal static AutomationPassResult NoWork { get; } = new(false, null);

        internal static AutomationPassResult Complete { get; } = new(false, null, true);
    }
}
