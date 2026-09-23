using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationActionExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ContinuousLoop_ExecutesTheSelectedActionThroughAutomationDesk()
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "hero_manage",
            Group = QueueGroup.Hero,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now,
        };
        var execution = new InMemoryAutomationActionExecutionPort(item);
        var executor = new AutomationActionExecutor(execution);
        var reads = new Queue<AutomationStateSnapshot>(
        [
            new([AutomationCandidate.FromQueueItem(item)]),
            new([], IsComplete: true),
        ]);
        var modePass = new DelegateAutomationModePassPort(
            _ => ValueTask.FromResult(reads.Dequeue()),
            (action, token) => executor.ExecuteAsync(AutomationRunMode.ContinuousLoop, action, token));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            modePass,
            modePass,
            new FixedTimeProvider(Now));
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
            ["record:LOOP 11", "execute:ContinuousLoop", "activity", "cooldown"],
            execution.Trace);
    }

    [Fact]
    public async Task MissingQueueItem_IsSkippedWithoutSideEffects()
    {
        var item = QueueItemFor("hero_manage", QueueGroup.Hero);
        var execution = new InMemoryAutomationActionExecutionPort(item);

        var outcome = await ExecuteThroughAutomationDeskAsync(
            AutomationRunMode.ContinuousLoop,
            AutomationCandidate.FromQueueItem(item) with { Id = Guid.NewGuid() },
            execution);

        Assert.Equal(AutomationActionOutcome.Skipped, outcome);
        Assert.Empty(execution.Trace);
    }

    [Fact]
    public async Task ContinuousLoopStop_BlocksBeforePacingOrExecution()
    {
        var item = QueueItemFor("hero_manage", QueueGroup.Hero);
        var execution = new InMemoryAutomationActionExecutionPort(item)
        {
            LoopStopRequested = true,
        };

        var outcome = await ExecuteThroughAutomationDeskAsync(
            AutomationRunMode.ContinuousLoop,
            AutomationCandidate.FromQueueItem(item),
            execution);

        Assert.Equal(AutomationActionOutcome.Blocked, outcome);
        Assert.Empty(execution.Trace);
    }

    [Fact]
    public async Task AutoQueue_PreparesChromiumAndUsesItsRunContext()
    {
        var item = QueueItemFor("upgrade_building", QueueGroup.Construction);
        var execution = new InMemoryAutomationActionExecutionPort(item);

        var outcome = await ExecuteThroughAutomationDeskAsync(
            AutomationRunMode.AutoQueue,
            AutomationCandidate.FromQueueItem(item),
            execution);

        Assert.Equal(AutomationActionOutcome.Completed, outcome);
        Assert.Equal(
            ["chromium", "record:AUTOQ 12", "execute:AutoQueue", "cooldown"],
            execution.Trace);
    }

    private static async Task<AutomationActionOutcome> ExecuteThroughAutomationDeskAsync(
        AutomationRunMode mode,
        AutomationCandidate action,
        InMemoryAutomationActionExecutionPort execution)
    {
        var executor = new AutomationActionExecutor(execution);
        var reads = new Queue<AutomationStateSnapshot>(
        [
            new([action]),
            new([], IsComplete: true),
        ]);
        var modePass = new DelegateAutomationModePassPort(
            _ => ValueTask.FromResult(reads.Dequeue()),
            (candidate, token) => executor.ExecuteAsync(mode, candidate, token));
        using var loopController = new LoopController();
        await using var automation = new AutomationDesk(
            loopController,
            modePass,
            modePass,
            new FixedTimeProvider(Now));
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
            mode,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        return (await finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).Outcome;
    }

    private static QueueItem QueueItemFor(string taskName, QueueGroup group) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Group = group,
        Status = QueueStatus.Pending,
        NextAttemptAt = Now,
    };

    private sealed class InMemoryAutomationActionExecutionPort(QueueItem item)
        : IAutomationActionExecutionPort
    {
        public List<string> Trace { get; } = [];
        public long ContinuousPassId => 11;
        public long AutoQueueRunLogId => 12;
        public bool LoopStopRequested { get; init; }
        public bool QueueStopRequested { get; init; }
        public QueueItem? FindQueueItem(Guid id) => id == item.Id ? item : null;
        public BotOptions LoadOptions() => new()
        {
            ActionPacingTaskMinSeconds = 0,
            ActionPacingTaskMaxSeconds = 0,
        };
        public ValueTask EnsureChromiumInstalledAsync()
        {
            Trace.Add("chromium");
            return ValueTask.CompletedTask;
        }
        public void RecordVillageBatchAttempt(QueueItem queueItem, string source) =>
            Trace.Add($"record:{source}");
        public ValueTask<bool> ExecuteQueueItemAsync(
            QueueItem queueItem,
            BotOptions options,
            AutomationRunMode mode,
            string logPrefix,
            CancellationToken cancellationToken)
        {
            Trace.Add($"execute:{mode}");
            return ValueTask.FromResult(true);
        }
        public void MarkContinuousBrowserActivity(BotOptions options) => Trace.Add("activity");
        public ValueTask ApplyPostTaskCooldownAsync(
            QueueItem queueItem,
            BotOptions options,
            CancellationToken cancellationToken)
        {
            Trace.Add("cooldown");
            return ValueTask.CompletedTask;
        }
        public void Log(string message) { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
