using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SessionSleepLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ManualSleep_OwnsCaptureStopCloseAndBeginSequence()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ContinuousLoopRunning = true },
        };
        var lifecycle = CreateLifecycle(pacer, port);

        await lifecycle.RequestManualSleepAsync();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Equal(SessionSleepReason.Manual, pacer.SleepReason);
        AssertOrder(port.Trace, "stop:AfterCurrentAction", "offline", "stop-all", "close:Session sleep");
        Assert.Equal(("Session sleep", true), Assert.Single(port.CloseRequests));
        Assert.False(lifecycle.IsSleepInProgress);
        Assert.False(lifecycle.IsManualSleepRequested);
    }

    [Fact]
    public async Task ControlledSleep_WhenBrowserCloseFails_DoesNotPublishSleepingState()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true },
            CloseException = new InvalidOperationException("tracked Chromium remained open"),
        };
        var lifecycle = CreateLifecycle(pacer, port);

        await lifecycle.RequestManualSleepAsync();

        Assert.NotEqual(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Contains(port.Logs, line => line.Contains("sleep was not started", StringComparison.Ordinal));
        Assert.Contains("close:Session sleep", port.Trace);
    }

    [Fact]
    public async Task PlannedSleep_ClosesBrowserBeforePublishingSleepingState()
    {
        var pacer = new SessionPacer(() => Now);
        pacer.Configure(new SessionPacerSettings(
            true,
            60,
            60,
            30,
            30,
            AllowedHours: [11]));
        var port = new InMemorySleepPort { State = DefaultState() };
        var lifecycle = CreateLifecycle(pacer, port);

        var entered = await lifecycle.TryEnterPlannedSleepInsteadOfLoginAsync();

        Assert.True(entered);
        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Equal(["reload-config", "close:Planned sleep", "ui"], port.Trace);
        Assert.Equal(("Planned sleep", false), Assert.Single(port.CloseRequests));
    }

    [Fact]
    public async Task PlannedSleep_WhenBrowserCloseFails_PropagatesAndDoesNotPublishSleepingState()
    {
        var pacer = new SessionPacer(() => Now);
        pacer.Configure(new SessionPacerSettings(
            true,
            60,
            60,
            30,
            30,
            AllowedHours: [11]));
        var port = new InMemorySleepPort
        {
            State = DefaultState(),
            CloseException = new InvalidOperationException("cleanup failed"),
        };
        var lifecycle = CreateLifecycle(pacer, port);

        await Assert.ThrowsAsync<InvalidOperationException>(
            lifecycle.TryEnterPlannedSleepInsteadOfLoginAsync);

        Assert.NotEqual(SessionPacerPhase.Sleeping, pacer.Phase);
    }

    [Fact]
    public async Task Wake_RetriesLoginAndRestoresTheCapturedContinuousLoop()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ContinuousLoopRunning = true },
        };
        port.LoginResults.Enqueue(false);
        port.LoginResults.Enqueue(true);
        var lifecycle = CreateLifecycle(pacer, port);
        await lifecycle.RequestManualSleepAsync();
        pacer.WakeNow();

        await lifecycle.WakeAsync();

        Assert.Equal(2, port.LoginAttempts);
        Assert.Contains("resume-continuous", port.Trace);
        Assert.DoesNotContain("resume-auto-queue", port.Trace);
        Assert.Contains(port.Logs, line => line.Contains("succeeded on attempt 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wake_RestoresTheCapturedAutoQueueWithoutStartingContinuousLoop()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, AutoQueueRunning = true },
        };
        var lifecycle = CreateLifecycle(pacer, port);
        await lifecycle.RequestManualSleepAsync();
        pacer.WakeNow();

        await lifecycle.WakeAsync();

        Assert.Contains("resume-auto-queue", port.Trace);
        Assert.DoesNotContain("resume-continuous", port.Trace);
    }

    [Fact]
    public async Task Wake_WhenTheCapturedSessionWasLoggedOut_DoesNotAttemptLogin()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort { State = DefaultState() };
        var lifecycle = CreateLifecycle(pacer, port);

        await lifecycle.WakeAsync();

        Assert.Equal(0, port.LoginAttempts);
        Assert.Contains(port.Logs, line => line.Contains("staying idle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reset_ClearsTheCapturedResumeIntent()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ContinuousLoopRunning = true },
        };
        var lifecycle = CreateLifecycle(pacer, port);
        await lifecycle.RequestManualSleepAsync();

        lifecycle.Reset();
        await lifecycle.WakeAsync();

        Assert.Equal(0, port.LoginAttempts);
        Assert.DoesNotContain("resume-continuous", port.Trace);
    }

    [Fact]
    public async Task SmartSleep_WhenFillShrinksOpportunity_StaysOnlineWithoutClosingBrowser()
    {
        var now = Now;
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            60,
            60,
            30,
            30,
            RunTimerEnabled: false));
        pacer.NotifyAutomationStarted();
        Assert.True(pacer.RequestSmartSleep(now.AddMinutes(10)));
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ContinuousLoopRunning = true },
            SmartSleepMinimumOpportunityMinutes = 20,
            FillResult = new SessionPreSleepFillResult(2, 1, "completed-or-deferred"),
        };
        var lifecycle = CreateLifecycle(pacer, port, () => now);

        await lifecycle.StartAutomaticSleepAsync();

        Assert.DoesNotContain(port.Trace, entry => entry.StartsWith("close:", StringComparison.Ordinal));
        Assert.Equal(SessionPacerPhase.Running, pacer.Phase);
        Assert.Contains(port.Logs, line => line.Contains("opportunity-shrunk-after-fill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActiveOperation_DefersTheWholeTransactionUntilTheOperationEnds()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ActiveOperation = true },
        };
        var lifecycle = CreateLifecycle(pacer, port);

        await lifecycle.StartAutomaticSleepAsync();

        Assert.DoesNotContain(port.Trace, entry => entry.StartsWith("close:", StringComparison.Ordinal));

        port.State = port.State with { ActiveOperation = false };
        await lifecycle.StartDeferredSleepAsync();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Contains("close:Session sleep", port.Trace);
    }

    [Fact]
    public async Task WakeLoginRetry_StopsWhenApplicationStartsClosing()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ContinuousLoopRunning = true },
        };
        port.LoginResults.Enqueue(false);
        var lifecycle = new SessionSleepLifecycle(
            pacer,
            port,
            () => Now,
            _ =>
            {
                port.State = port.State with { AppClosing = true };
                return Task.CompletedTask;
            });
        await lifecycle.RequestManualSleepAsync();
        pacer.WakeNow();

        await lifecycle.WakeAsync();

        Assert.Equal(1, port.LoginAttempts);
        Assert.DoesNotContain("resume-continuous", port.Trace);
        Assert.Contains(port.Logs, line => line.Contains("stopped during wait", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlannedSleepDuringWakeRetry_PreservesTheOriginalResumeIntent()
    {
        var pacer = new SessionPacer(() => Now);
        var port = new InMemorySleepPort
        {
            State = DefaultState() with { LoggedIn = true, ContinuousLoopRunning = true },
        };
        var lifecycle = CreateLifecycle(pacer, port);
        port.LoginHandler = async () =>
        {
            pacer.Configure(new SessionPacerSettings(
                true,
                60,
                60,
                30,
                30,
                AllowedHours: [11]));
            Assert.True(await lifecycle.TryEnterPlannedSleepInsteadOfLoginAsync());
            return false;
        };
        await lifecycle.RequestManualSleepAsync();
        pacer.WakeNow();

        await lifecycle.WakeAsync();
        Assert.DoesNotContain("resume-continuous", port.Trace);

        port.LoginHandler = null;
        pacer.WakeNow();
        await lifecycle.WakeAsync();

        Assert.Contains("resume-continuous", port.Trace);
    }

    [Fact]
    public async Task LoginVillageRound_DefersPlannedSleepThenLifecycleRetriesIt()
    {
        var now = Now;
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(true, 1, 1, 30, 30));
        pacer.NotifyAutomationStarted();
        now = now.AddMinutes(2);
        pacer.TickForTests();
        var port = new InMemorySleepPort
        {
            State = DefaultState() with
            {
                LoggedIn = true,
                ContinuousLoopRunning = true,
                LoginRoundPending = true,
            },
        };
        var lifecycle = CreateLifecycle(pacer, port, () => now);

        await lifecycle.StartAutomaticSleepAsync();

        Assert.NotNull(lifecycle.VillageRoundDeferredUntilUtc);
        Assert.DoesNotContain(port.Trace, entry => entry.StartsWith("close:", StringComparison.Ordinal));

        port.State = port.State with { LoginRoundPending = false };
        await lifecycle.PollVillageRoundDeferredSleepAsync();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Contains("close:Session sleep", port.Trace);
    }

    private static SessionSleepLifecycle CreateLifecycle(
        SessionPacer pacer,
        InMemorySleepPort port,
        Func<DateTimeOffset>? now = null) => new(
            pacer,
            port,
            now ?? (() => Now),
            _ => Task.CompletedTask);

    private static SessionSleepHostState DefaultState() => new(
        FreezeActive: false,
        ActiveOperation: false,
        LoggedIn: false,
        ContinuousLoopRunning: false,
        AutoQueueRunning: false,
        LoginRoundPending: false,
        LoginInProgress: false,
        AccountSwitchInProgress: false,
        AppClosing: false);

    private static void AssertOrder(IReadOnlyList<string> trace, params string[] expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            var index = trace.ToList().FindIndex(previous + 1, value => value == item);
            Assert.True(index > previous, $"Expected '{item}' after index {previous}. Trace: {string.Join(", ", trace)}");
            previous = index;
        }
    }

    private sealed class InMemorySleepPort : ISessionSleepLifecyclePort
    {
        internal SessionSleepHostState State { get; set; } = DefaultState();

        internal List<string> Trace { get; } = [];

        internal List<string> Logs { get; } = [];

        internal List<(string OperationName, bool ShowBusyState)> CloseRequests { get; } = [];

        internal Queue<bool> LoginResults { get; } = [];

        internal Exception? CloseException { get; init; }

        internal int SmartSleepMinimumOpportunityMinutes { get; init; } = 20;

        internal SessionPreSleepFillResult FillResult { get; init; } = SessionPreSleepFillResult.None;

        internal int LoginAttempts { get; private set; }

        internal Func<Task<bool>>? LoginHandler { get; set; }

        public SessionSleepHostState ReadState() => State;

        public int ReadVillageRoundSleepExtensionMinutes() => 5;

        public int ReadSmartSleepMinimumOpportunityMinutes() => SmartSleepMinimumOpportunityMinutes;

        public ValueTask<SessionPreSleepFillResult> WaitForPreSleepFillAsync()
        {
            Trace.Add("fill");
            return ValueTask.FromResult(FillResult);
        }

        public void RequestAutomationStop(AutomationStopMode mode)
        {
            Trace.Add($"stop:{mode}");
            if (mode == AutomationStopMode.AfterCurrentAction)
            {
                State = State with { ContinuousLoopRunning = false, AutoQueueRunning = false };
            }
        }

        public void CancelActiveOperation() => Trace.Add("cancel-operation");

        public void PrepareOfflineForSleep()
        {
            Trace.Add("offline");
            State = State with { LoggedIn = false };
        }

        public Task StopAllAutomationAsync()
        {
            Trace.Add("stop-all");
            return Task.CompletedTask;
        }

        public Task CloseBrowserForSleepAsync(string operationName, bool showBusyState)
        {
            Trace.Add($"close:{operationName}");
            CloseRequests.Add((operationName, showBusyState));
            return CloseException is null ? Task.CompletedTask : Task.FromException(CloseException);
        }

        public void ActivatePendingProxyAtSleep() => Trace.Add("activate-proxy");

        public void ReloadPacerConfiguration() => Trace.Add("reload-config");

        public Task ApplyProxyPlanForWakeAsync(DateTimeOffset wakeAt)
        {
            Trace.Add("plan-proxy-wake");
            return Task.CompletedTask;
        }

        public void UpdateSessionActivity() => Trace.Add("session-activity");

        public void UpdateUi() => Trace.Add("ui");

        public async Task<bool> LoginForWakeAsync()
        {
            LoginAttempts++;
            var loggedIn = LoginHandler is not null
                ? await LoginHandler()
                : LoginResults.Count == 0 || LoginResults.Dequeue();
            State = State with { LoggedIn = loggedIn };
            Trace.Add($"login:{loggedIn}");
            return loggedIn;
        }

        public bool ConsumeForcedVillageRoundOnWake() => false;

        public void ForceVillageRound() => Trace.Add("force-village-round");

        public void ResumeContinuousLoop()
        {
            Trace.Add("resume-continuous");
            State = State with { ContinuousLoopRunning = true };
        }

        public void ResumeAutoQueue()
        {
            Trace.Add("resume-auto-queue");
            State = State with { AutoQueueRunning = true };
        }

        public void Log(string message) => Logs.Add(message);
    }
}
