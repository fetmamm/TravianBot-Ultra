using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousAutomationPassTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadyCandidate_IsPublishedAfterPreparation()
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "hero_manage",
            Group = QueueGroup.Hero,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now.AddMinutes(-1),
        };
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            SelectedItems = new Queue<QueueItem?>([item, null]),
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass);
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
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        var published = await selected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(item.Id, published.Item.Value);
        Assert.Equal(1, preparation.ActivePassCount);
        Assert.True(
            preparation.Trace.IndexOf("membership") < preparation.Trace.IndexOf("village-status-round"));
        Assert.True(
            preparation.Trace.IndexOf("membership") < preparation.Trace.IndexOf("select"));
    }

    [Fact]
    public async Task ReadyTroopTraining_PublishesSmartSleepBlockerDiagnostic()
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "build_troops",
            Group = QueueGroup.TroopTraining,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now,
            IsRuntimeOnly = true,
        };
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            SelectedItems = new Queue<QueueItem?>([item]),
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));

        var snapshot = await pass.ReadAsync(
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7),
            CancellationToken.None);

        Assert.Single(snapshot.Candidates);
        Assert.Equal([item.Id], preparation.SmartSleepBlockers);
    }

    [Fact]
    public async Task NoReadyWork_WaitsUntilThePreparedWakeDeadline()
    {
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            Deadlines = new ContinuousAutomationDeadlineSnapshot(
                Now.AddSeconds(75), null, null, [], SmartSleepDeadlinePolicy.AllGroups.ToHashSet()),
        };
        var delay = new ControlledDelay();
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass, delay.WaitAsync);

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(TimeSpan.FromSeconds(75), await delay.Requested.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DeferredTroopTrainingDeadline_DrivesSmartSleepWhileVillageScanOnlyDrivesOnlineWake()
    {
        var troopDeadline = Now.AddHours(2);
        var villageScanDeadline = Now.AddMinutes(5);
        var deferredTroops = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "build_troops",
            Group = QueueGroup.TroopTraining,
            Status = QueueStatus.Pending,
            NextAttemptAt = troopDeadline,
            IsRuntimeOnly = true,
        };
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            Options = new BotOptions
            {
                LoopIntervalSeconds = 60,
                ContinuousKeepAliveEnabled = true,
            },
            NextKeepAliveAtUtc = Now.AddMinutes(2),
            SmartSleepRequestAccepted = true,
            Deadlines = new ContinuousAutomationDeadlineSnapshot(
                troopDeadline,
                null,
                villageScanDeadline,
                [deferredTroops],
                SmartSleepDeadlinePolicy.AllGroups.ToHashSet()),
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));

        var snapshot = await pass.ReadAsync(
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7),
            CancellationToken.None);

        Assert.Equal(troopDeadline, preparation.RequestedSmartSleepDeadline);
        Assert.Equal(villageScanDeadline, snapshot.NextWakeAt);
    }

    [Fact]
    public async Task DeadlineWake_ChecksWorkBeforeVillageStatusRoundThenRetriesAfterIt()
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "construct_building",
            Group = QueueGroup.Construction,
            Status = QueueStatus.Pending,
            NextAttemptAt = Now,
        };
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            PrioritizeDeadlineWorkOnWake = true,
            SelectedItems = new Queue<QueueItem?>([null, item, null]),
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass);
        var selected = new TaskCompletionSource<AutomationEvent.ActionSelected>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var villageStatusRoundsAtSelection = -1;
        var runtimeItemPassesAtSelection = -1;
        automation.Updated += (_, update) =>
        {
            if (update.Event is AutomationEvent.ActionSelected actionSelected)
            {
                villageStatusRoundsAtSelection = preparation.VillageStatusRoundCount;
                runtimeItemPassesAtSelection = preparation.RuntimeItemsCount;
                selected.TrySetResult(actionSelected);
            }
        };

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(item.Id, (await selected.Task.WaitAsync(TimeSpan.FromSeconds(2))).Item.Value);
        Assert.Equal(1, villageStatusRoundsAtSelection);
        Assert.Equal(2, runtimeItemPassesAtSelection);
        Assert.False(preparation.PrioritizeDeadlineWorkOnWake);
    }

    [Fact]
    public async Task NetworkBackoff_WaitsWithoutPreparingBrowserWork()
    {
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            NetworkBackoffRemaining = TimeSpan.FromSeconds(45),
        };
        var delay = new ControlledDelay();
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass, delay.WaitAsync);

        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        Assert.Equal(TimeSpan.FromSeconds(45), await delay.Requested.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, preparation.ChromiumPreparationCount);
    }

    [Fact]
    public async Task ProxyRecovery_CompletesTheRunBeforeBrowserPreparation()
    {
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            ProxyRecoveryResults = new Queue<bool>([true]),
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass);
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
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, preparation.ChromiumPreparationCount);
    }

    [Fact]
    public async Task Cancellation_StopsBlockedPreparation()
    {
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            BlockMembershipVerification = true,
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass);
        await automation.StartAsync(new AutomationStart(
            AutomationRunMode.ContinuousLoop,
            new AutomationRunContext("account-1", new Uri("https://ts1.x1.example/"), 7)));
        await preparation.MembershipVerificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await automation.StopAsync(AutomationStopMode.CancelCurrentAction);

        Assert.Equal(AutomationPhase.Stopped, automation.Current.Phase);
    }

    [Fact]
    public async Task AccountAccessFailure_HoldsTheAccountBeforePublishingFailure()
    {
        var preparation = new InMemoryContinuousAutomationPassPort
        {
            MembershipFailure = new AccountAccessException(
                "account-1",
                AccountAccessState.Restricted,
                "restricted"),
        };
        var pass = new ContinuousAutomationPass(preparation, new FixedTimeProvider(Now));
        using var loopController = new LoopController();
        await using var automation = CreateAutomationDesk(loopController, pass);
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
        Assert.Equal(AutomationFailureKind.AccountAccess, failure.Failure.Kind);
        Assert.Equal(1, preparation.AccountHoldCount);
    }

    private static AutomationDesk CreateAutomationDesk(
        LoopController loopController,
        IAutomationModePassPort continuousPass,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var pass = new AutomationPassPort(
            () => "account-1",
            () => 7,
            continuousPass,
            new DelegateAutomationModePassPort(
                _ => ValueTask.FromResult(new AutomationStateSnapshot([], IsComplete: true)),
                (_, _) => ValueTask.FromResult(AutomationActionOutcome.Skipped)));
        return new AutomationDesk(
            loopController,
            pass,
            pass,
            new FixedTimeProvider(Now),
            delayAsync);
    }

    private sealed class InMemoryContinuousAutomationPassPort : IContinuousAutomationPassPort
    {
        public Queue<QueueItem?> SelectedItems { get; init; } = new([null]);
        public Queue<bool> ProxyRecoveryResults { get; init; } = new([false]);
        public int ActivePassCount { get; private set; }
        public int VillageStatusRoundCount { get; private set; }
        public int RuntimeItemsCount { get; private set; }
        public int ChromiumPreparationCount { get; private set; }
        public int AccountHoldCount { get; private set; }
        public List<Guid> SmartSleepBlockers { get; } = [];
        public DateTimeOffset? RequestedSmartSleepDeadline { get; private set; }
        public BotOptions Options { get; init; } = new() { LoopIntervalSeconds = 60 };
        public bool SmartSleepRequestAccepted { get; init; }
        public List<string> Trace { get; } = [];
        public ContinuousAutomationDeadlineSnapshot Deadlines { get; init; } = new(
            Now.AddMinutes(1), null, null, [], SmartSleepDeadlinePolicy.AllGroups.ToHashSet());
        public bool BlockMembershipVerification { get; init; }
        public Exception? MembershipFailure { get; init; }
        public TaskCompletionSource ChromiumPreparationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MembershipVerificationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BotOptions LoadOptions() => Options;
        public long BeginPass() => 1;
        public bool TryScheduleAutomaticProxyRecovery(BotOptions options) =>
            ProxyRecoveryResults.Count > 0 && ProxyRecoveryResults.Dequeue();
        public TimeSpan NetworkBackoffRemaining { get; init; }
        public DateTimeOffset VillageMembershipVerificationNotBeforeUtc => DateTimeOffset.MinValue;
        public DateTimeOffset NextKeepAliveAtUtc { get; init; } = DateTimeOffset.MaxValue;
        public bool PrioritizeDeadlineWorkOnWake { get; set; }
        public bool HasPendingLoginRound { get; init; }
        public QueueItem? SelectReadyPriorityQueueItem(BotOptions options) => null;
        public ValueTask EnsureChromiumInstalledAsync()
        {
            ChromiumPreparationCount++;
            ChromiumPreparationStarted.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public async ValueTask<bool> EnsureVillageMembershipVerifiedAsync(BotOptions options, CancellationToken cancellationToken)
        {
            Trace.Add("membership");
            MembershipVerificationStarted.TrySetResult();
            if (BlockMembershipVerification)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (MembershipFailure is not null)
            {
                throw MembershipFailure;
            }

            return true;
        }
        public bool ConsumeImmediateWorkRequest() => false;
        public ValueTask MaybeTakeIdleBreakAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask MaybeDoIdleBrowseAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public bool ConsumeForceVillageStatusRoundRequest() => false;
        public ValueTask MaybeRunVillageStatusRoundAsync(BotOptions options, CancellationToken cancellationToken, bool force)
        {
            Trace.Add("village-status-round");
            VillageStatusRoundCount++;
            return ValueTask.CompletedTask;
        }
        public ValueTask EnsureConstructionStatusAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask MaybeAnalyzeNewVillageAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask EnsureRuntimeItemsAsync(BotOptions options, CancellationToken cancellationToken)
        {
            RuntimeItemsCount++;
            return ValueTask.CompletedTask;
        }
        public ValueTask MaybeCheckInboxAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public QueueItem? SelectNextQueueItem()
        {
            Trace.Add("select");
            return SelectedItems.Count == 0 ? null : SelectedItems.Dequeue();
        }
        public void MarkActivePass() => ActivePassCount++;
        public void LogSmartSleepBlockedByReadyTask(QueueItem item) => SmartSleepBlockers.Add(item.Id);
        public ValueTask MaybeKeepBrowserFreshAsync(BotOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ContinuousAutomationDeadlineSnapshot ReadDeadlines(BotOptions options) => Deadlines;
        public bool TryRequestSmartSleep(DateTimeOffset? trustedDeadlineUtc)
        {
            RequestedSmartSleepDeadline = trustedDeadlineUtc;
            return SmartSleepRequestAccepted;
        }
        public bool ShouldPublishIdleHeartbeat(TimeSpan interval) => false;
        public void Log(string message) { }
        public string FormatException(Exception exception) => exception.Message;
        public ValueTask HoldAccountAutomationAsync(AccountAccessException exception)
        {
            AccountHoldCount++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<AutomationActionOutcome> ExecuteAsync(AutomationCandidate action, CancellationToken cancellationToken) =>
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
