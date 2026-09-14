using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueItemLifecycleTests
{
    [Fact]
    public async Task DisabledItem_IsSkippedBeforeExecutionScope()
    {
        var port = new InMemoryQueueItemLifecyclePort { Allowed = false };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[LOOP 1]", AutomationRunMode.ContinuousLoop, default);

        Assert.True(shouldContinue);
        Assert.Empty(port.Trace);
        Assert.Contains("automation is disabled", port.Logs.Single());
    }

    [Fact]
    public async Task DisabledAfterRunning_IsDeferredAndFinalizedWithoutWorkerCall()
    {
        var port = new InMemoryQueueItemLifecyclePort { DisableAfterMarkRunning = true };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[LOOP 1]", AutomationRunMode.ContinuousLoop, default);

        Assert.True(shouldContinue);
        Assert.Equal(["scope", "running", "deferred", "finalize"], port.Trace);
    }

    [Fact]
    public async Task HandledGuard_FinalizesWithFreshRefreshFlag()
    {
        var port = new InMemoryQueueItemLifecyclePort
        {
            GuardResult = new QueueItemGuardResult(true, true),
        };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[LOOP 1]", AutomationRunMode.ContinuousLoop, default);

        Assert.True(shouldContinue);
        Assert.Equal(["scope", "running", "guards", "finalize"], port.Trace);
    }

    [Fact]
    public async Task Cancellation_DefersItemAndStopsPass()
    {
        var port = new InMemoryQueueItemLifecyclePort
        {
            WorkerException = new OperationCanceledException(),
        };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[LOOP 1]", AutomationRunMode.ContinuousLoop, default);

        Assert.False(shouldContinue);
        Assert.Equal(["scope", "running", "guards", "worker", "deferred", "finalize"], port.Trace);
    }

    [Fact]
    public async Task Failure_IsDelegatedAndFinalized()
    {
        var port = new InMemoryQueueItemLifecyclePort
        {
            WorkerException = new InvalidOperationException("boom"),
            FailureOutcome = true,
        };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[AUTOQ 2]", AutomationRunMode.AutoQueue, default);

        Assert.True(shouldContinue);
        Assert.Equal(["scope", "running", "guards", "worker", "failure", "finalize"], port.Trace);
    }

    [Fact]
    public async Task DemolitionNavigationRace_DefersWithoutConsumingRetries()
    {
        var port = new InMemoryQueueItemLifecyclePort
        {
            Demolition = true,
            WorkerException = new InvalidOperationException("Execution context was destroyed during navigation"),
        };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[AUTOQ 2]", AutomationRunMode.AutoQueue, default);

        Assert.True(shouldContinue);
        Assert.Equal(TimeSpan.FromSeconds(15), port.DeferredDelay);
        Assert.DoesNotContain("failure", port.Trace);
        Assert.Contains("without consuming retries", port.Logs.Single());
    }

    [Fact]
    public async Task BrowserTargetCrash_DefersForFreshSession()
    {
        var port = new InMemoryQueueItemLifecyclePort
        {
            WorkerException = new InvalidOperationException("Target page, context or browser has been closed"),
        };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[LOOP 1]", AutomationRunMode.ContinuousLoop, default);

        Assert.True(shouldContinue);
        Assert.Equal(TimeSpan.FromSeconds(15), port.DeferredDelay);
        Assert.DoesNotContain("failure", port.Trace);
    }

    [Fact]
    public async Task UiThreadAccessFailure_DefersAndRaisesAlarm()
    {
        var port = new InMemoryQueueItemLifecyclePort
        {
            WorkerException = new InvalidOperationException("A different thread owns it"),
        };

        var shouldContinue = await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[LOOP 1]", AutomationRunMode.ContinuousLoop, default);

        Assert.True(shouldContinue);
        Assert.Equal(TimeSpan.FromMinutes(30), port.DeferredDelay);
        Assert.StartsWith("ALARM:", port.Logs.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("failure", port.Trace);
    }

    [Fact]
    public async Task AutoQueueBuildingMutation_RestoresSnapshotDuringFinalization()
    {
        var port = new InMemoryQueueItemLifecyclePort();
        var item = CreateItem("upgrade_building_to_level");

        await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            item, new BotOptions(), "[AUTOQ 2]", AutomationRunMode.AutoQueue, default);

        Assert.Equal(
            ["scope", "running", "guards", "worker", "succeeded", "healthy", "finalize", "restore"],
            port.Trace);
    }

    [Fact]
    public async Task Demolition_UsesOperationScopeAndCompletesItDuringFinalization()
    {
        var port = new InMemoryQueueItemLifecyclePort { Demolition = true };

        await new AutomationQueueItemLifecycle(port).ExecuteAsync(
            CreateItem(), new BotOptions(), "[AUTOQ 2]", AutomationRunMode.AutoQueue, default);

        Assert.Equal(
            ["scope", "running", "guards", "begin-demolition", "worker", "succeeded", "healthy", "complete-demolition", "finalize"],
            port.Trace);
    }

    [Fact]
    public async Task SuccessfulItem_CompletesItsLifecycleThroughAutomationDesk()
    {
        var item = CreateItem();
        var lifecyclePort = new InMemoryQueueItemLifecyclePort();
        var lifecycle = new AutomationQueueItemLifecycle(lifecyclePort);
        var reads = new Queue<AutomationStateSnapshot>(
        [
            new([AutomationCandidate.FromQueueItem(item)]),
            new([], IsComplete: true),
        ]);
        var modePass = new DelegateAutomationModePassPort(
            _ => ValueTask.FromResult(reads.Dequeue()),
            async (action, token) => await lifecycle.ExecuteAsync(
                item,
                new BotOptions(),
                "[LOOP 1]",
                AutomationRunMode.ContinuousLoop,
                token)
                    ? AutomationActionOutcome.Completed
                    : AutomationActionOutcome.Blocked);
        var pass = new AutomationPassPort(() => "account-1", () => 7, modePass, modePass);
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(loopController, pass, pass);
        var finished = new TaskCompletionSource<AutomationEvent.ActionFinished>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.ActionFinished actionFinished)
            {
                finished.TrySetResult(actionFinished);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(
            AutomationActionOutcome.Completed,
            (await finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
        Assert.Equal(
            ["scope", "running", "guards", "worker", "succeeded", "healthy", "last-scan", "finalize"],
            lifecyclePort.Trace);
    }

    private static QueueItem CreateItem(string taskName = "collect_tasks") => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Group = QueueGroup.Account,
        Status = QueueStatus.Pending,
        NextAttemptAt = DateTimeOffset.MinValue,
    };

    private sealed class InMemoryQueueItemLifecyclePort : IAutomationQueueItemLifecyclePort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public bool Allowed { get; set; } = true;
        public bool DisableAfterMarkRunning { get; init; }
        public QueueItemGuardResult GuardResult { get; init; } = QueueItemGuardResult.NotHandled;
        public Exception? WorkerException { get; init; }
        public bool FailureOutcome { get; init; }
        public bool Demolition { get; init; }
        public TimeSpan? DeferredDelay { get; private set; }
        public bool IsAllowedByAutomationSettings(QueueItem item) => Allowed;
        public IDisposable BeginExecutionScope(QueueItem item)
        {
            Trace.Add("scope");
            return new NoopDisposable();
        }
        public BotOptions LoadCurrentOptions() => new();
        public void MarkDueConstructionForPreSleepFill(QueueItem item) { }
        public void RefreshConstructFasterPayloadForExecution(QueueItem item) { }
        public bool MarkRunning(Guid itemId)
        {
            Trace.Add("running");
            if (DisableAfterMarkRunning)
            {
                Allowed = false;
            }
            return true;
        }
        public void RefreshQueueUi(Guid itemId) { }
        public void SetActiveAutomationTask(string? taskName) { }
        public void SetActiveFunctionExecution(string? displayName)
        {
            if (displayName is null)
            {
                Trace.Add("finalize");
            }
        }
        public ValueTask<QueueItemGuardResult> RunPreExecutionGuardsAsync(
            QueueItem item,
            BotOptions options,
            string logPrefix,
            Stopwatch timer,
            CancellationToken cancellationToken)
        {
            Trace.Add("guards");
            return ValueTask.FromResult(GuardResult);
        }
        public BotOptions ApplyQueueItemOptions(BotOptions options, QueueItem item) => options;
        public CancellationToken BeginDemolitionOperation(QueueItem item, CancellationToken cancellationToken)
        {
            Trace.Add("begin-demolition");
            return cancellationToken;
        }
        public ValueTask<BotTaskExecutionResult> ExecuteWorkerAsync(
            BotOptions options,
            QueueItem item,
            CancellationToken cancellationToken)
        {
            Trace.Add("worker");
            if (WorkerException is not null)
            {
                return ValueTask.FromException<BotTaskExecutionResult>(WorkerException);
            }
            return ValueTask.FromResult(BotTaskExecutionResult.Empty);
        }
        public ValueTask<bool> TryRecoverMissingBuildingUpgradeAsync(
            QueueItem item,
            BotOptions options,
            BotTaskExecutionResult executionResult,
            string logPrefix,
            Stopwatch timer,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> HandleSucceededAsync(
            QueueItem item,
            BotOptions options,
            BotTaskExecutionResult executionResult,
            CancellationToken cancellationToken)
        {
            Trace.Add("succeeded");
            return ValueTask.FromResult(false);
        }
        public bool IsLoadBuildingsSnapshot(QueueItem item) => false;
        public ValueTask LoadBuildingsSnapshotAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void MarkNetworkConnectionHealthy() => Trace.Add("healthy");
        public void PublishLastScan() => Trace.Add("last-scan");
        public bool IsDemolition(QueueItem item) => Demolition;
        public bool WasDemolitionStopped(Guid itemId) => false;
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            Trace.Add("deferred");
            DeferredDelay = delay;
            return true;
        }
        public TimeSpan NextNetworkRetryDelay() => TimeSpan.FromSeconds(20);
        public void MarkNetworkUnavailable(TimeSpan retryDelay) { }
        public ValueTask HoldAccountAutomationAsync(AccountAccessException exception) => ValueTask.CompletedTask;
        public ValueTask HandleUnexpectedTravianLanguageAsync(
            UnexpectedTravianLanguageException exception) => ValueTask.CompletedTask;
        public ValueTask<bool> HandleTaskSpecificFailureAsync(
            QueueItem item,
            Exception exception,
            string logPrefix,
            Stopwatch timer,
            AutomationRunMode mode)
        {
            Trace.Add("failure");
            return ValueTask.FromResult(FailureOutcome);
        }
        public void CompleteDemolitionOperation(Guid itemId) => Trace.Add("complete-demolition");
        public ValueTask RestoreBuildingsSnapshotAsync(CancellationToken cancellationToken)
        {
            Trace.Add("restore");
            return ValueTask.CompletedTask;
        }
        public void Log(string message) => Logs.Add(message);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
