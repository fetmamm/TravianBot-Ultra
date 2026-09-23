using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationDeskTests
{
    [Fact]
    public async Task StartAsync_PublishesTheAuthoritativeRunningSnapshot()
    {
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(loopController);
        AutomationUpdate? published = null;
        automation.Updated += (_, update) => published = update;

        var result = await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var started = Assert.IsType<AutomationStartResult.Started>(result);
        Assert.Equal(started.RunId, automation.Current.RunId);
        Assert.Equal(AutomationPhase.Running, automation.Current.Phase);
        Assert.NotNull(published);
        Assert.IsType<AutomationEvent.RunStarted>(published.Event);
        Assert.Equal(automation.Current, published.Snapshot);
    }

    [Fact]
    public async Task StartAsync_IsNotChangedByAThrowingUpdateSubscriber()
    {
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(loopController);
        automation.Updated += (_, _) => throw new InvalidOperationException("presentation failed");

        var result = await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.IsType<AutomationStartResult.Started>(result);
        Assert.Equal(AutomationPhase.Running, automation.Current.Phase);
    }

    [Fact]
    public async Task StartAsync_ExecutesOneReadyActionThroughAnAutomationPass()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var itemId = Guid.NewGuid();
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(itemId, "hero_manage", QueueGroup.Hero, "0|0", 0, now.AddMinutes(-1))]));
        var officialTravian = new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed);
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, officialTravian),
            CreateModePass(state, officialTravian),
            new FixedTimeProvider(now));
        var completed = new TaskCompletionSource<AutomationEvent.ActionFinished>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.ActionFinished actionFinished)
            {
                completed.TrySetResult(actionFinished);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var action = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(itemId, action.Item.Value);
        Assert.Equal(AutomationActionOutcome.Completed, action.Outcome);
    }

    [Fact]
    public async Task Updates_ArePublishedInRunOrderEvenWhenTheFirstActionIsImmediate()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(Guid.NewGuid(), "hero_manage", QueueGroup.Hero, "0|0", 0, now)]));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            new FixedTimeProvider(now));
        var eventTypes = new List<Type>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            lock (eventTypes)
            {
                eventTypes.Add(update.Event.GetType());
            }
            if (update.Event is AutomationEvent.ActionFinished)
            {
                completed.TrySetResult();
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (eventTypes)
        {
            Assert.Equal(
                [
                    typeof(AutomationEvent.RunStarted),
                    typeof(AutomationEvent.ActionSelected),
                    typeof(AutomationEvent.ActionFinished),
                ],
                eventTypes.Take(3));
        }
    }

    [Fact]
    public async Task Wake_RechecksStateAndExecutesNewlyReadyWork()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var itemId = Guid.NewGuid();
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(itemId, "hero_manage", QueueGroup.Hero, "0|0", 0, now.AddMinutes(5))]));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            new FixedTimeProvider(now));
        var completed = new TaskCompletionSource<AutomationEvent.ActionFinished>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.ActionFinished actionFinished)
            {
                completed.TrySetResult(actionFinished);
            }
        };
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));
        await state.FirstRead.WaitAsync(TimeSpan.FromSeconds(2));

        state.Replace(new AutomationStateSnapshot(
            [new AutomationCandidate(itemId, "hero_manage", QueueGroup.Hero, "0|0", 0, now)]));
        var wake = automation.Wake(AutomationWakeReason.QueueChanged);

        Assert.Equal(AutomationWakeResult.Accepted, wake);
        var action = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(itemId, action.Item.Value);
    }

    [Fact]
    public async Task FutureWork_IsRecheckedAtItsAuthoritativeDeadline()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var itemId = Guid.NewGuid();
        var time = new MutableTimeProvider(now);
        var delay = new ControlledDelay();
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(itemId, "hero_manage", QueueGroup.Hero, "0|0", 0, now.AddMinutes(5))]));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            time,
            delay.WaitAsync);
        var completed = new TaskCompletionSource<AutomationEvent.ActionFinished>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.ActionFinished actionFinished)
            {
                completed.TrySetResult(actionFinished);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(TimeSpan.FromMinutes(5), await delay.Requested.WaitAsync(TimeSpan.FromSeconds(2)));
        time.Advance(TimeSpan.FromMinutes(5));
        delay.Complete();

        var action = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(itemId, action.Item.Value);
    }

    [Fact]
    public async Task AutoQueue_StopsWhenTheStateReportsNoRemainingWork()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot([], IsComplete: true));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            CreateModePass(state, new InMemoryOfficialTravianPort(AutomationActionOutcome.Completed)),
            new FixedTimeProvider(now));
        var stopped = new TaskCompletionSource<AutomationEvent.RunStopped>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.RunStopped runStopped)
            {
                stopped.TrySetResult(runStopped);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.AutoQueue,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutomationPhase.Stopped, automation.Current.Phase);
    }

    [Fact]
    public async Task AutoQueue_StartIsBusyWhileAnotherDeskOwnsTheLoopControllerGate()
    {
        using var loopController = new LoopController();
        await using var first = new AutomationDesk(loopController);
        await using var second = new AutomationDesk(loopController);
        var start = new AutomationStart(
            AutomationRunMode.AutoQueue,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7));

        Assert.IsType<AutomationStartResult.Started>(await first.StartAsync(start));

        Assert.IsType<AutomationStartResult.Busy>(await second.StartAsync(start));
    }

    [Fact]
    public async Task LoopControllerStopRequest_CompletesTheAuthoritativeRun()
    {
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(loopController);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.RunStopped)
            {
                stopped.TrySetResult();
            }
        };
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.AutoQueue,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        loopController.RequestQueueStop();

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutomationPhase.Stopped, automation.Current.Phase);
    }

    [Fact]
    public async Task StopAsync_AfterCurrentActionWaitsForTheSafeBoundary()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(Guid.NewGuid(), "hero_manage", QueueGroup.Hero, "0|0", 0, now)]));
        var officialTravian = new BlockingOfficialTravianPort();
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, officialTravian),
            CreateModePass(state, officialTravian),
            new FixedTimeProvider(now));
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));
        await officialTravian.Started.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = automation.StopAsync(AutomationStopMode.AfterCurrentAction).AsTask();

        Assert.False(stopping.IsCompleted);
        officialTravian.Complete();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutomationPhase.Stopped, automation.Current.Phase);
    }

    [Fact]
    public async Task StopAsync_CancelCurrentActionEscalatesAnInProgressGracefulStop()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(Guid.NewGuid(), "hero_manage", QueueGroup.Hero, "0|0", 0, now)]));
        var officialTravian = new BlockingOfficialTravianPort();
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, officialTravian),
            CreateModePass(state, officialTravian),
            new FixedTimeProvider(now));
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));
        await officialTravian.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var gracefulStop = automation.StopAsync(AutomationStopMode.AfterCurrentAction).AsTask();

        var forcedStop = automation.StopAsync(AutomationStopMode.CancelCurrentAction).AsTask();

        await Task.WhenAll(gracefulStop, forcedStop).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutomationPhase.Stopped, automation.Current.Phase);
    }

    [Fact]
    public async Task StartAsync_ReplacementWaitsUntilTheOldGenerationIsQuiescent()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(Guid.NewGuid(), "hero_manage", QueueGroup.Hero, "0|0", 0, now)]));
        var officialTravian = new NonCancelableFirstActionPort();
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, officialTravian),
            CreateModePass(state, officialTravian),
            new FixedTimeProvider(now));
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));
        await officialTravian.FirstActionStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-2", new Uri("https://ts2.x1.example/"), 8))).AsTask();

        Assert.False(replacement.IsCompleted);
        officialTravian.CompleteFirstAction();
        Assert.IsType<AutomationStartResult.Replaced>(
            await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("account-2", automation.Current.Context?.AccountKey);
        Assert.Equal(8, automation.Current.Context?.BrowserGeneration);
    }

    [Fact]
    public async Task AdapterFailure_PublishesAnAtomicFaultedUpdate()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(Guid.NewGuid(), "hero_manage", QueueGroup.Hero, "0|0", 0, now)]));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, new ThrowingOfficialTravianPort()),
            CreateModePass(state, new ThrowingOfficialTravianPort()),
            new FixedTimeProvider(now));
        var faulted = new TaskCompletionSource<AutomationUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.RunFaulted)
            {
                faulted.TrySetResult(update);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var update = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failure = Assert.IsType<AutomationEvent.RunFaulted>(update.Event);
        Assert.Equal(AutomationFailureKind.AdapterContract, failure.Failure.Kind);
        Assert.Equal(AutomationPhase.Faulted, update.Snapshot.Phase);
        Assert.Equal(update.Snapshot, automation.Current);
    }

    [Fact]
    public async Task StaleBrowserGeneration_PublishesItsTypedTerminalFailure()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            new ThrowingAutomationModePass(new AutomationContextException(
                AutomationFailureKind.StaleBrowserGeneration,
                "browser-generation-changed",
                "changed")),
            new ThrowingAutomationModePass(new AutomationContextException(
                AutomationFailureKind.StaleBrowserGeneration,
                "browser-generation-changed",
                "changed")),
            new FixedTimeProvider(now));
        var faulted = new TaskCompletionSource<AutomationEvent.RunFaulted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.RunFaulted failure)
            {
                faulted.TrySetResult(failure);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var failure = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutomationFailureKind.StaleBrowserGeneration, failure.Failure.Kind);
        Assert.Equal("browser-generation-changed", failure.Failure.DiagnosticCode);
        Assert.False(failure.Failure.IsRetryable);
    }

    [Fact]
    public async Task TransientNavigationFailure_PublishesARetryableTypedFailure()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            new ThrowingAutomationModePass(new TransientNavigationException("navigation timed out")),
            new ThrowingAutomationModePass(new TransientNavigationException("navigation timed out")),
            new FixedTimeProvider(now));
        var deferred = new TaskCompletionSource<AutomationEvent.RunDeferred>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.RunDeferred failure)
            {
                deferred.TrySetResult(failure);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var failure = await deferred.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutomationFailureKind.TransientNetwork, failure.Failure.Kind);
        Assert.True(failure.Failure.IsRetryable);
        Assert.Equal(AutomationPhase.Running, automation.Current.Phase);
    }

    [Fact]
    public async Task Respond_ApprovesThePendingDecisionBeforeExecution()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var itemId = Guid.NewGuid();
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot(
            [new AutomationCandidate(
                itemId,
                "hero_manage",
                QueueGroup.Hero,
                "0|0",
                0,
                now,
                new AutomationDecisionRequirement("confirm_hero_change"))]));
        var officialTravian = new RecordingOfficialTravianPort();
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, officialTravian),
            CreateModePass(state, officialTravian),
            new FixedTimeProvider(now));
        var decisionRequested = new TaskCompletionSource<AutomationEvent.DecisionRequested>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<AutomationEvent.ActionFinished>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.DecisionRequested decision)
            {
                decisionRequested.TrySetResult(decision);
            }
            else if (update.Event is AutomationEvent.ActionFinished finished)
            {
                completed.TrySetResult(finished);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var request = await decisionRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("confirm_hero_change", request.Decision.Code);
        Assert.Equal(0, officialTravian.CallCount);
        Assert.Equal(
            AutomationDecisionResult.Accepted,
            automation.Respond(request.Decision.RequestId, AutomationDecisionChoice.Approve));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, officialTravian.CallCount);
        Assert.Null(automation.Current.PendingDecision);
    }

    [Fact]
    public async Task Respond_RejectsDuplicateAndStaleRunDecisions()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var candidate = new AutomationCandidate(
            Guid.NewGuid(),
            "hero_manage",
            QueueGroup.Hero,
            "0|0",
            0,
            now,
            new AutomationDecisionRequirement("confirm"));
        var state = new InMemoryAutomationStatePort(new AutomationStateSnapshot([candidate]));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            CreateModePass(state, new RecordingOfficialTravianPort()),
            CreateModePass(state, new RecordingOfficialTravianPort()),
            new FixedTimeProvider(now));
        var firstRequest = new TaskCompletionSource<AutomationDecisionRequestId>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.DecisionRequested requested)
            {
                firstRequest.TrySetResult(requested.Decision.RequestId);
            }
        };
        var firstContext = new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7);
        await automation.StartAsync(new AutomationStart(AutomationRunMode.ContinuousLoop, firstContext));
        var answeredId = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            AutomationDecisionResult.Accepted,
            automation.Respond(answeredId, AutomationDecisionChoice.Decline));
        Assert.Equal(
            AutomationDecisionResult.AlreadyAnswered,
            automation.Respond(answeredId, AutomationDecisionChoice.Decline));

        state.Replace(new AutomationStateSnapshot([candidate with { Id = Guid.NewGuid() }]));
        var staleRequest = new TaskCompletionSource<AutomationDecisionRequestId>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.DecisionRequested requested
                && requested.Decision.RequestId != answeredId)
            {
                staleRequest.TrySetResult(requested.Decision.RequestId);
            }
        };
        automation.Wake(AutomationWakeReason.QueueChanged);
        var staleId = await staleRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-2", new Uri("https://ts2.x1.example/"), 8)));

        Assert.Equal(
            AutomationDecisionResult.StaleRun,
            automation.Respond(staleId, AutomationDecisionChoice.Approve));
    }

    private static IAutomationModePassPort CreateModePass(
        InMemoryAutomationStatePort state,
        ITestAutomationExecutor executor) => new DelegateAutomationModePassPort(
            cancellationToken => state.ReadAsync(cancellationToken),
            (action, cancellationToken) => executor.ExecuteAsync(action, cancellationToken),
            (action, outcome, cancellationToken) => state.CompleteAsync(action, outcome, cancellationToken));

    private interface ITestAutomationExecutor
    {
        ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken);
    }

    private sealed class InMemoryAutomationStatePort(AutomationStateSnapshot snapshot)
    {
        private AutomationStateSnapshot _snapshot = snapshot;
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstRead => _firstRead.Task;

        public void Replace(AutomationStateSnapshot replacement) => _snapshot = replacement;

        public ValueTask<AutomationStateSnapshot> ReadAsync(
            CancellationToken cancellationToken)
        {
            _firstRead.TrySetResult();
            return ValueTask.FromResult(_snapshot);
        }

        public ValueTask CompleteAsync(
            AutomationCandidate action,
            AutomationActionOutcome outcome,
            CancellationToken cancellationToken)
        {
            _snapshot = new AutomationStateSnapshot(
                _snapshot.Candidates.Where(candidate => candidate.Id != action.Id).ToList());

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryOfficialTravianPort(AutomationActionOutcome outcome) : ITestAutomationExecutor
    {
        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken) => ValueTask.FromResult(outcome);
    }

    private sealed class ThrowingAutomationModePass(Exception exception) : IAutomationModePassPort
    {
        public ValueTask<AutomationStateSnapshot> ReadAsync(
            AutomationRunContext context,
            CancellationToken cancellationToken) => ValueTask.FromException<AutomationStateSnapshot>(exception);

        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationRunContext context,
            AutomationCandidate action,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AutomationActionOutcome.Skipped);

        public ValueTask CompleteAsync(
            AutomationRunContext context,
            AutomationCandidate action,
            AutomationActionOutcome outcome,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class BlockingOfficialTravianPort : ITestAutomationExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Complete() => _completion.TrySetResult();

        public async ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
            return AutomationActionOutcome.Completed;
        }
    }

    private sealed class NonCancelableFirstActionPort : ITestAutomationExecutor
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstActionStarted => _firstStarted.Task;

        public void CompleteFirstAction() => _firstCompletion.TrySetResult();

        public async ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _firstStarted.TrySetResult();
                await _firstCompletion.Task;
            }

            return AutomationActionOutcome.Completed;
        }
    }

    private sealed class ThrowingOfficialTravianPort : ITestAutomationExecutor
    {
        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AutomationActionOutcome>(new InvalidOperationException("adapter failed"));
    }

    private sealed class RecordingOfficialTravianPort : ITestAutomationExecutor
    {
        public int CallCount { get; private set; }

        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(AutomationActionOutcome.Completed);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource<TimeSpan> _requested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> Requested => _requested.Task;

        public void Complete() => _completion.TrySetResult();

        public async Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            _requested.TrySetResult(duration);
            await _completion.Task.WaitAsync(cancellationToken);
        }
    }
}
