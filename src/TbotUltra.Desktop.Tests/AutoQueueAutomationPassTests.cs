using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutoQueueAutomationPassTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadyCandidate_IsPublishedThroughAutomationDesk()
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "upgrade_building",
            Group = QueueGroup.Construction,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now,
        };
        var preparation = new InMemoryAutoQueueAutomationPassPort
        {
            SelectedItems = new Queue<QueueItem?>([item, null]),
        };
        var autoQueuePass = new AutoQueueAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, autoQueuePass);
        var selected = new TaskCompletionSource<AutomationEvent.ActionSelected>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.ActionSelected actionSelected)
            {
                selected.TrySetResult(actionSelected);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.AutoQueue,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(item.Id, (await selected.Task.WaitAsync(TimeSpan.FromSeconds(2))).Item.Value);
        Assert.False(preparation.PrioritizeDeadlineWorkOnWake);
    }

    [Fact]
    public async Task EmptyQueue_CompletesTheAutoQueueRun()
    {
        var preparation = new InMemoryAutoQueueAutomationPassPort();
        var autoQueuePass = new AutoQueueAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, autoQueuePass);
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
        Assert.Contains("DONE (queue empty)", preparation.LogMessages.Single());
    }

    [Fact]
    public async Task DeferredWork_SchedulesSmartSleepAndWaitsForItsDeadline()
    {
        var deferred = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "upgrade_building",
            Group = QueueGroup.Construction,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now.AddMinutes(3),
        };
        var preparation = new InMemoryAutoQueueAutomationPassPort
        {
            QueueItems = [deferred],
        };
        var delay = new ControlledDelay();
        var autoQueuePass = new AutoQueueAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, autoQueuePass, delay.WaitAsync);

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.AutoQueue,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(TimeSpan.FromMinutes(3), await delay.Requested.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(Now.AddMinutes(3), preparation.RequestedSmartSleepDeadline);
    }

    [Fact]
    public async Task DeferredConstruction_UsesQueueClearOnlyForSmartSleepDeadline()
    {
        var deferred = new QueueItem
        {
            TaskName = "upgrade_building",
            Group = QueueGroup.Construction,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now.AddMinutes(30),
        };
        var queueClear = Now.AddHours(2);
        var preparation = new InMemoryAutoQueueAutomationPassPort
        {
            QueueItems = [deferred],
            SmartSleepQueueDeadlineOverrides = new Dictionary<Guid, DateTimeOffset>
            {
                [deferred.Id] = queueClear,
            },
        };
        var delay = new ControlledDelay();
        var autoQueuePass = new AutoQueueAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, autoQueuePass, delay.WaitAsync);

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.AutoQueue,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(TimeSpan.FromMinutes(30), await delay.Requested.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(queueClear, preparation.RequestedSmartSleepDeadline);
    }

    private static AutomationDesk CreateAutomationDesk(
        LoopController loopController,
        IAutomationModePassPort autoQueuePass,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var pass = new AutomationPassPort(
            () => "account-1",
            () => 7,
            new DelegateAutomationModePassPort(
                _ => ValueTask.FromResult(new AutomationStateSnapshot([], IsComplete: true)),
                (_, _) => ValueTask.FromResult(AutomationActionOutcome.Skipped)),
            autoQueuePass);
        return new AutomationDesk(
            loopController,
            pass,
            pass,
            new FixedTimeProvider(Now),
            delayAsync);
    }

    private sealed class InMemoryAutoQueueAutomationPassPort : IAutoQueueAutomationPassPort
    {
        public bool HasPendingLoginRound => false;
        public QueueItem? SelectReadyPriorityQueueItem(BotOptions options) => null;
        public ValueTask RunPendingLoginRoundAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public Queue<QueueItem?> SelectedItems { get; init; } = new([null]);
        public IReadOnlyList<QueueItem> QueueItems { get; init; } = [];
        public List<string> LogMessages { get; } = [];
        public DateTimeOffset? RequestedSmartSleepDeadline { get; private set; }
        public IReadOnlyDictionary<Guid, DateTimeOffset> SmartSleepQueueDeadlineOverrides { get; init; } =
            new Dictionary<Guid, DateTimeOffset>();
        public bool PrioritizeDeadlineWorkOnWake { get; set; } = true;
        public long RunLogId => 7;
        public IReadOnlySet<QueueGroup> SmartSleepDeadlineGroups { get; } =
            SmartSleepDeadlinePolicy.AllGroups.ToHashSet();
        public BotOptions LoadOptionsWithSelectedVillage() => new();
        public ValueTask HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public QueueItem? SelectNextQueueItem() => SelectedItems.Count == 0 ? null : SelectedItems.Dequeue();
        public IReadOnlyList<QueueItem> GetQueueItems() => QueueItems;
        public IReadOnlyDictionary<Guid, DateTimeOffset> GetSmartSleepQueueDeadlineOverrides(
            IReadOnlyList<QueueItem> items,
            DateTimeOffset now) => SmartSleepQueueDeadlineOverrides;
        public bool IsAllowedByAutomationSettings(QueueItem item) => true;
        public bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc)
        {
            RequestedSmartSleepDeadline = trustedDeadlineUtc;
            return false;
        }
        public void Log(string message) => LogMessages.Add(message);
        public ValueTask<AutomationActionOutcome> ExecuteAsync(
            AutomationCandidate action,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AutomationActionOutcome.Completed);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource<TimeSpan> _requested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> Requested => _requested.Task;

        public async Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            _requested.TrySetResult(duration);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
