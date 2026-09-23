using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed record SessionSleepHostState(
    bool FreezeActive,
    bool ActiveOperation,
    bool LoggedIn,
    bool ContinuousLoopRunning,
    bool AutoQueueRunning,
    bool LoginRoundPending,
    bool LoginInProgress,
    bool AccountSwitchInProgress,
    bool AppClosing);

internal sealed record SessionPreSleepFillResult(int TrackedCount, int StartedCount, string Outcome)
{
    internal static readonly SessionPreSleepFillResult None = new(0, 0, "none");
}

internal interface ISessionSleepLifecyclePort
{
    SessionSleepHostState ReadState();

    int ReadVillageRoundSleepExtensionMinutes();

    int ReadSmartSleepMinimumOpportunityMinutes();

    ValueTask<SessionPreSleepFillResult> WaitForPreSleepFillAsync();

    void RequestAutomationStop(AutomationStopMode mode);

    void CancelActiveOperation();

    void PrepareOfflineForSleep();

    Task StopAllAutomationAsync();

    Task CloseBrowserForSleepAsync(string operationName, bool showBusyState);

    void ActivatePendingProxyAtSleep();

    void ReloadPacerConfiguration();

    Task ApplyProxyPlanForWakeAsync(DateTimeOffset wakeAt);

    void UpdateSessionActivity();

    void UpdateUi();

    Task<bool> LoginForWakeAsync();

    bool ConsumeForcedVillageRoundOnWake();

    void ForceVillageRound();

    void ResumeContinuousLoop();

    void ResumeAutoQueue();

    void Log(string message);
}

/// <summary>
/// Owns the complete Desktop sleep transaction: capture, stop, close, wake, login, and restore.
/// The WPF/browser work remains behind an internal adapter seam.
/// </summary>
internal sealed class SessionSleepLifecycle
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(130);
    private static readonly TimeSpan WakeRetryPollInterval = TimeSpan.FromSeconds(2);
    private static readonly int[] WakeLoginRetryBackoffMinutes = [1, 2, 5, 10, 15, 30];
    private readonly SessionPacer _pacer;
    private readonly ISessionSleepLifecyclePort _port;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, Task> _delayAsync;
    private SleepSnapshot _snapshot = SleepSnapshot.Idle;
    private DateTimeOffset? _villageRoundDeferredUntilUtc;
    private bool _villageRoundRetryScheduled;

    private sealed record SleepSnapshot(
        bool WasLoggedIn,
        bool WasContinuousLoopRunning,
        bool WasQueueAutoRunning)
    {
        internal static readonly SleepSnapshot Idle = new(false, false, false);
    }

    private enum WakeResumeAction
    {
        StayLoggedIn,
        ResumeContinuousLoop,
        ResumeQueueAutoRun,
    }

    private readonly record struct WakeAbortDecision(bool ShouldAbort, string Reason);

    internal SessionSleepLifecycle(
        SessionPacer pacer,
        ISessionSleepLifecyclePort port,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        _pacer = pacer;
        _port = port;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    internal bool IsSleepInProgress { get; private set; }

    internal bool IsWakeInProgress { get; private set; }

    internal bool IsManualSleepRequested { get; private set; }

    private bool IsSleepDeferredForActiveOperation { get; set; }

    internal DateTimeOffset? VillageRoundDeferredUntilUtc => _villageRoundDeferredUntilUtc;

    internal async Task RequestManualSleepAsync()
    {
        if (_pacer.Phase == SessionPacerPhase.Sleeping || IsSleepInProgress || IsManualSleepRequested)
        {
            _port.Log("[pacing] manual sleep ignored: sleep is already active or starting.");
            return;
        }

        _snapshot = CaptureSnapshot();
        IsManualSleepRequested = true;
        _port.RequestAutomationStop(AutomationStopMode.AfterCurrentAction);
        _port.Log("[pacing] manual sleep requested; waiting for the current action, then starting no new work.");
        _port.UpdateUi();
        await StartSleepAsync(manual: true).ConfigureAwait(true);
    }

    internal Task StartAutomaticSleepAsync() => StartSleepAsync(manual: false);

    private async Task StartSleepAsync(bool manual)
    {
        var state = _port.ReadState();
        if (state.FreezeActive)
        {
            _port.Log("[pacing] sleep start skipped: freeze is active.");
            return;
        }

        if (IsSleepInProgress)
        {
            return;
        }

        if (!manual
            && state.LoginRoundPending
            && (state.ContinuousLoopRunning || state.AutoQueueRunning)
            && _pacer.PendingSleepReason is SessionSleepReason.SessionPacing or SessionSleepReason.SmartSleep)
        {
            _villageRoundDeferredUntilUtc ??= _utcNow().AddMinutes(
                PacingDefaults.NormalizeVillageRoundSleepExtensionMinutes(
                    _port.ReadVillageRoundSleepExtensionMinutes()));
            if (_utcNow() < _villageRoundDeferredUntilUtc
                && _pacer.ActiveHardRestriction == SessionSleepReason.None)
            {
                _port.Log($"[village-round] planned sleep delayed while village round finishes; "
                    + $"up to {Math.Ceiling((_villageRoundDeferredUntilUtc.Value - _utcNow()).TotalSeconds)}s remaining.");
                return;
            }
        }

        _pacer.ApplyActiveHardRestrictionToPendingSleep();
        _villageRoundDeferredUntilUtc = null;

        if (state.ActiveOperation)
        {
            if (!IsSleepDeferredForActiveOperation)
            {
                _port.Log("[pacing] sleep delayed until the active manual operation finishes.");
            }

            IsSleepDeferredForActiveOperation = true;
            return;
        }

        IsSleepInProgress = true;
        try
        {
            if (!manual)
            {
                _snapshot = CaptureSnapshot();
            }

            _port.Log($"[pacing] pre-sleep state: loggedIn={_snapshot.WasLoggedIn}, "
                + $"continuousLoop={_snapshot.WasContinuousLoopRunning}, queueAutoRun={_snapshot.WasQueueAutoRunning}.");

            if (!manual && !await ValidatePreShutdownOpportunityAsync().ConfigureAwait(true))
            {
                return;
            }

            if (!await RequestGracefulAutomationStopAsync().ConfigureAwait(true))
            {
                _port.Log("[pacing] graceful stop timed out; canceling the remaining automation.");
            }

            _port.Log("[pacing] controlled session stop requested.");
            _port.RequestAutomationStop(AutomationStopMode.CancelCurrentAction);
            _port.CancelActiveOperation();
            _port.PrepareOfflineForSleep();
            await _port.StopAllAutomationAsync().ConfigureAwait(true);

            try
            {
                await _port.CloseBrowserForSleepAsync("Session sleep", showBusyState: true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _port.Log($"[pacing] session browser close failed; sleep was not started: {ex.Message}");
                return;
            }

            _port.ActivatePendingProxyAtSleep();
            _port.ReloadPacerConfiguration();
            _pacer.BeginSleep(manual);
            IsManualSleepRequested = false;
            if (_pacer.PlannedWakeAt is { } wakeAt)
            {
                await _port.ApplyProxyPlanForWakeAsync(wakeAt).ConfigureAwait(true);
            }

            _port.UpdateSessionActivity();
        }
        finally
        {
            IsSleepInProgress = false;
            if (manual && _pacer.Phase != SessionPacerPhase.Sleeping)
            {
                IsManualSleepRequested = false;
            }

            _port.UpdateUi();
        }
    }

    internal async Task StartDeferredSleepAsync()
    {
        if (!IsSleepDeferredForActiveOperation)
        {
            return;
        }

        IsSleepDeferredForActiveOperation = false;
        var manual = IsManualSleepRequested;
        var state = _port.ReadState();
        if (state.FreezeActive
            || _pacer.Phase == SessionPacerPhase.Sleeping
            || IsSleepInProgress
            || state.ActiveOperation)
        {
            return;
        }

        _port.Log(manual
            ? "[pacing] delayed manual sleep starting after the current operation completed."
            : "[pacing] delayed automatic sleep starting after manual operation completed.");
        await StartSleepAsync(manual).ConfigureAwait(true);
    }

    internal async Task PollVillageRoundDeferredSleepAsync()
    {
        if (_villageRoundDeferredUntilUtc is null)
        {
            return;
        }

        if (_pacer.PendingSleepReason == SessionSleepReason.None)
        {
            _villageRoundDeferredUntilUtc = null;
            _port.Log("[village-round] planned sleep was canceled; the village round continues.");
            return;
        }

        var state = _port.ReadState();
        if (_villageRoundRetryScheduled
            || (state.LoginRoundPending
                && (state.ContinuousLoopRunning || state.AutoQueueRunning)
                && _utcNow() < _villageRoundDeferredUntilUtc
                && _pacer.ActiveHardRestriction == SessionSleepReason.None))
        {
            return;
        }

        _villageRoundRetryScheduled = true;
        try
        {
            await StartAutomaticSleepAsync().ConfigureAwait(true);
        }
        finally
        {
            _villageRoundRetryScheduled = false;
        }
    }

    internal async Task WakeAsync()
    {
        var state = _port.ReadState();
        if (state.FreezeActive)
        {
            _port.Log("[pacing] wake skipped: freeze is active.");
            return;
        }

        if (IsWakeInProgress)
        {
            return;
        }

        IsWakeInProgress = true;
        try
        {
            state = _port.ReadState();
            if (state.LoginInProgress || state.AccountSwitchInProgress)
            {
                _port.Log("[pacing] wake skipped: login or account switch already in progress.");
                return;
            }

            if (!_snapshot.WasLoggedIn)
            {
                _port.Log("[pacing] wake: was logged out/idle before sleep — staying idle.");
                return;
            }

            if (!await TryWakeLoginWithRetryAsync().ConfigureAwait(true))
            {
                return;
            }

            if (_port.ConsumeForcedVillageRoundOnWake())
            {
                _port.ForceVillageRound();
                _port.Log("[smart-sleep] fallback wake will run one Village Status Round.");
            }

            state = _port.ReadState();
            switch (ResolveResume(
                        _snapshot,
                        loopIdle: !state.ContinuousLoopRunning,
                        state.AutoQueueRunning))
            {
                case WakeResumeAction.ResumeContinuousLoop:
                    _port.Log("[pacing] wake: resuming continuous loop (was running before sleep).");
                    _port.ResumeContinuousLoop();
                    break;
                case WakeResumeAction.ResumeQueueAutoRun:
                    _port.Log("[pacing] wake: resuming queue auto-run (was running before sleep).");
                    _port.ResumeAutoQueue();
                    break;
                default:
                    _port.Log("[pacing] wake: logged in, staying idle (was not running before sleep).");
                    break;
            }
        }
        finally
        {
            IsWakeInProgress = false;
        }
    }

    internal async Task<bool> TryEnterPlannedSleepInsteadOfLoginAsync()
    {
        var state = _port.ReadState();
        if (state.LoginInProgress || state.AccountSwitchInProgress || IsSleepInProgress)
        {
            return false;
        }

        _port.ReloadPacerConfiguration();
        if (!_pacer.ShouldSleepNow())
        {
            return false;
        }

        _snapshot = new SleepSnapshot(true, false, false);
        await _port.CloseBrowserForSleepAsync("Planned sleep", showBusyState: false).ConfigureAwait(true);
        if (!_pacer.BeginScheduledSleepNow())
        {
            return false;
        }

        _port.Log("[login] planned sleep window is active — entering sleep instead of logging in. "
            + "Press the session pacing Run-now button to log in anyway.");
        _port.UpdateUi();
        return true;
    }

    internal void PrepareExplicitWakeLogin() => _snapshot = new SleepSnapshot(true, false, false);

    internal void Reset()
    {
        _snapshot = SleepSnapshot.Idle;
        IsSleepInProgress = false;
        IsWakeInProgress = false;
        IsSleepDeferredForActiveOperation = false;
        IsManualSleepRequested = false;
        _villageRoundDeferredUntilUtc = null;
        _villageRoundRetryScheduled = false;
        _pacer.Reset();
    }

    private SleepSnapshot CaptureSnapshot()
    {
        var state = _port.ReadState();
        return new SleepSnapshot(
            state.LoggedIn,
            state.ContinuousLoopRunning,
            state.AutoQueueRunning);
    }

    private async Task<bool> ValidatePreShutdownOpportunityAsync()
    {
        var fill = await _port.WaitForPreSleepFillAsync().ConfigureAwait(true);
        if (_pacer.PendingSleepReason != SessionSleepReason.SmartSleep
            || _pacer.PendingSmartWakeAt is not { } pendingWakeAt)
        {
            return true;
        }

        var remaining = pendingWakeAt - _utcNow();
        var minimum = TimeSpan.FromMinutes(Math.Max(1, _port.ReadSmartSleepMinimumOpportunityMinutes()));
        if (remaining < minimum && _pacer.CancelPendingSmartSleep())
        {
            _port.Log($"[smart-sleep] decision=stay-online reason=opportunity-shrunk-after-fill "
                + $"remaining={FormatPositiveDuration(remaining)} minimum={FormatPositiveDuration(minimum)} "
                + $"fillTracked={fill.TrackedCount} fillStarted={fill.StartedCount} fillOutcome={fill.Outcome}.");
            return false;
        }

        _port.Log($"[smart-sleep] pre-shutdown validation passed: "
            + $"remaining={FormatPositiveDuration(remaining)}, minimum={FormatPositiveDuration(minimum)}, "
            + $"fillTracked={fill.TrackedCount}, fillStarted={fill.StartedCount}, fillOutcome={fill.Outcome}.");
        return true;
    }

    private async Task<bool> RequestGracefulAutomationStopAsync()
    {
        _port.RequestAutomationStop(AutomationStopMode.AfterCurrentAction);
        var deadline = _utcNow() + GracefulStopTimeout;
        var announced = false;
        while (_utcNow() < deadline)
        {
            var state = _port.ReadState();
            if (!state.AutoQueueRunning && !state.ContinuousLoopRunning && !state.ActiveOperation)
            {
                return true;
            }

            if (!announced)
            {
                announced = true;
                _port.Log("[pacing] waiting for the current action to finish before sleep; no new action will start.");
            }

            await _delayAsync(TimeSpan.FromMilliseconds(Random.Shared.Next(150, 350))).ConfigureAwait(true);
        }

        return false;
    }

    private async Task<bool> TryWakeLoginWithRetryAsync()
    {
        var resumeContinuousLoop = _snapshot.WasContinuousLoopRunning;
        var resumeQueueAutoRun = _snapshot.WasQueueAutoRunning;

        for (var attempt = 1; ; attempt++)
        {
            if (await _port.LoginForWakeAsync().ConfigureAwait(true))
            {
                if (attempt > 1)
                {
                    _port.Log($"[pacing] wake login succeeded on attempt {attempt}.");
                }

                return true;
            }

            if (_pacer.Phase == SessionPacerPhase.Sleeping)
            {
                _snapshot = new SleepSnapshot(true, resumeContinuousLoop, resumeQueueAutoRun);
                _port.Log("[pacing] wake retry: a planned sleep window took over; automation will resume after it.");
                return false;
            }

            var abort = ResolveWakeAbort();
            if (abort.ShouldAbort)
            {
                _port.Log($"[pacing] wake login retry stopped: {abort.Reason}.");
                return false;
            }

            var wait = NextWakeLoginRetryDelay(attempt);
            _port.Log($"[pacing] wake login failed (attempt {attempt}) — retrying in {wait.TotalMinutes:0} min.");
            if (!await DelayWhileWakeRetryAllowedAsync(wait).ConfigureAwait(true))
            {
                _port.Log("[pacing] wake login retry stopped during wait (state changed or app closing).");
                return false;
            }
        }
    }

    private WakeAbortDecision ResolveWakeAbort()
    {
        var state = _port.ReadState();
        return ResolveAbort(
            state.LoggedIn,
            _pacer.Phase == SessionPacerPhase.Sleeping,
            state.AccountSwitchInProgress,
            state.AppClosing);
    }

    private async Task<bool> DelayWhileWakeRetryAllowedAsync(TimeSpan total)
    {
        var remaining = total;
        while (remaining > TimeSpan.Zero)
        {
            if (ResolveWakeAbort().ShouldAbort)
            {
                return false;
            }

            var wait = remaining < WakeRetryPollInterval ? remaining : WakeRetryPollInterval;
            await _delayAsync(wait).ConfigureAwait(true);
            remaining -= wait;
        }

        return !ResolveWakeAbort().ShouldAbort;
    }

    private static string FormatPositiveDuration(TimeSpan duration) =>
        SessionPacer.FormatDuration(duration > TimeSpan.Zero ? duration : TimeSpan.Zero);

    private static WakeResumeAction ResolveResume(
        SleepSnapshot snapshot,
        bool loopIdle,
        bool autoQueueRunning)
    {
        if (snapshot.WasContinuousLoopRunning && loopIdle)
        {
            return WakeResumeAction.ResumeContinuousLoop;
        }

        if (snapshot.WasQueueAutoRunning && !autoQueueRunning && loopIdle)
        {
            return WakeResumeAction.ResumeQueueAutoRun;
        }

        return WakeResumeAction.StayLoggedIn;
    }

    private static TimeSpan NextWakeLoginRetryDelay(int attempt)
    {
        var index = Math.Clamp(attempt - 1, 0, WakeLoginRetryBackoffMinutes.Length - 1);
        return TimeSpan.FromMinutes(WakeLoginRetryBackoffMinutes[index]);
    }

    private static WakeAbortDecision ResolveAbort(
        bool isLoggedIn,
        bool isSessionSleeping,
        bool accountSwitchInProgress,
        bool appClosing)
    {
        if (isLoggedIn)
        {
            return new WakeAbortDecision(true, "already logged in");
        }

        if (isSessionSleeping)
        {
            return new WakeAbortDecision(true, "session is sleeping again");
        }

        if (accountSwitchInProgress)
        {
            return new WakeAbortDecision(true, "account switch in progress");
        }

        if (appClosing)
        {
            return new WakeAbortDecision(true, "app closing");
        }

        return new WakeAbortDecision(false, string.Empty);
    }
}
