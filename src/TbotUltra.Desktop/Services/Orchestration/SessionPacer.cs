using System.Windows.Threading;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services.Orchestration;

public enum SessionPacerPhase
{
    Disabled,
    Paused,
    Running,
    Sleeping,
}

public enum SessionSleepReason
{
    None,
    SessionPacing,
    Manual,
    Schedule,
    DailyLimit,
}

public sealed record SessionPacerSettings(
    bool Enabled,
    int RunMinMinutes,
    int RunMaxMinutes,
    int SleepMinMinutes,
    int SleepMaxMinutes,
    IReadOnlyList<int>? AllowedHours = null,
    int DailyMaxHours = 0,
    DateOnly? RuntimeDate = null,
    double RuntimeSeconds = 0,
    int DailyMaxVariationPercent = PacingDefaults.SessionPacingDailyMaxVariationPercent,
    int HoursVariationPercent = PacingDefaults.SessionPacingHoursVariationPercent);

public sealed record SessionPacerRuntimeState(DateOnly Date, double RuntimeSeconds);

public sealed record SessionPacerDailyProgress(
    DateOnly Date,
    TimeSpan OnlineToday,
    TimeSpan? TimeLeft,
    TimeSpan? Limit,
    int ConfiguredDailyMaxHours);

public sealed class SessionPacer
{
    private readonly DispatcherTimer _timer;
    private readonly Func<DateTimeOffset> _now;
    private SessionPacerSettings _settings = new(
        PacingDefaults.SessionPacingEnabled,
        PacingDefaults.SessionPacingRunMinMinutes,
        PacingDefaults.SessionPacingRunMaxMinutes,
        PacingDefaults.SessionPacingSleepMinMinutes,
        PacingDefaults.SessionPacingSleepMaxMinutes,
        DailyMaxHours: PacingDefaults.SessionPacingDailyMaxHours);
    private HashSet<int> _allowedHours = Enumerable.Range(0, 24).ToHashSet();
    private DateTimeOffset? _runStartedAt;
    private DateTimeOffset? _runDeadline;
    private DateTimeOffset? _wakeAt;
    private DateTimeOffset? _lastRuntimeUpdate;
    private DateTimeOffset? _lastRuntimePersist;
    private TimeSpan? _activeRunDuration;
    private TimeSpan? _activeSleepDuration;
    private TimeSpan? _pausedRunRemaining;
    private DateOnly _runtimeDate;
    private double _runtimeSeconds;
    private bool _sleepStartRaised;
    private bool _automationActive;
    private bool _manualSleep;
    private bool _manualOperationPaused;
    private bool _runtimeLoaded;
    private SessionSleepReason _pendingSleepReason;
    private DateTimeOffset? _proxyTransitionAt;
    // When set, a manual "Run now" override is suppressing the schedule restriction until this time
    // (the next moment the schedule would allow running on its own), so the bot can run through a
    // disallowed off-hours window the user explicitly chose to override.
    private DateTimeOffset? _scheduleOverrideUntil;

    public SessionPacer(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _runtimeDate = DateOnly.FromDateTime(_now().DateTime);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickTimer();
    }

    public Action<string>? Logger { get; set; }

    // Set by the host to report whether a scope-limited manual function (e.g. Analyze farmlists, Add farms,
    // Create farmlists, Travco) is currently running. While it returns true the run->sleep countdown is
    // frozen (see ReconcileManualOperationPause) so those functions don't get cut off by a sleep and the
    // bot doesn't sleep the instant they finish.
    public Func<bool>? IsManualOperationActive { get; set; }

    public SessionPacerPhase Phase { get; private set; } = SessionPacerPhase.Disabled;
    public SessionSleepReason SleepReason { get; private set; }
    public bool CanWakeNow => Phase == SessionPacerPhase.Sleeping
        && SleepReason is SessionSleepReason.SessionPacing or SessionSleepReason.Manual or SessionSleepReason.Schedule;
    public TimeSpan? TimeUntilSleep => _runDeadline is null ? null : Positive(_runDeadline.Value - _now());
    public TimeSpan? TimeUntilWake => _wakeAt is null ? null : Positive(_wakeAt.Value - _now());
    public TimeSpan? ActiveRunDuration => _activeRunDuration;
    public TimeSpan? ActiveSleepDuration => _activeSleepDuration;
    public DateTimeOffset? PlannedWakeAt => _wakeAt;
    public SessionPacerRuntimeState RuntimeState => new(_runtimeDate, _runtimeSeconds);
    public string StatusText => Phase switch
    {
        SessionPacerPhase.Running => $"Next sleep: {Format(TimeUntilSleep)}",
        SessionPacerPhase.Paused => "Paused",
        // All sleep reasons (session pacing, scheduled off-hours, daily limit, manual) show the same
        // "Sleeping: <countdown>" badge. The reason stays available via SleepReason / the Run-now button.
        SessionPacerPhase.Sleeping => $"Sleeping: {Format(TimeUntilWake)}",
        _ => _automationActive ? "Session pacing off" : "Session pacing",
    };

    public event EventHandler? SleepStarting;
    public event EventHandler? WakeRequested;
    public event EventHandler? RuntimeStateChanged;
    public event EventHandler? Tick;

    public void Configure(SessionPacerSettings settings, bool reloadRuntime = false)
    {
        var previousDailyMaxHours = _settings.DailyMaxHours;
        _settings = Normalize(settings);
        _allowedHours = (_settings.AllowedHours ?? Enumerable.Range(0, 24))
            .Where(hour => hour is >= 0 and <= 23)
            .ToHashSet();

        var now = _now();
        var today = DateOnly.FromDateTime(now.DateTime);
        if (reloadRuntime || !_runtimeLoaded)
        {
            _runtimeDate = today;
            _runtimeSeconds = _settings.RuntimeDate == today ? _settings.RuntimeSeconds : 0;
            _runtimeLoaded = true;
            _lastRuntimePersist = null;
        }
        else if (_runtimeDate != today)
        {
            _runtimeDate = today;
            _runtimeSeconds = 0;
        }
        else if (_settings.RuntimeDate == today)
        {
            _runtimeSeconds = Math.Max(_runtimeSeconds, _settings.RuntimeSeconds);
        }

        if (previousDailyMaxHours != _settings.DailyMaxHours)
        {
            _sleepStartRaised = false;
        }

        if (Phase == SessionPacerPhase.Sleeping)
        {
            if (SleepReason is SessionSleepReason.Schedule or SessionSleepReason.DailyLimit)
            {
                _wakeAt = ResolveRestrictionWake(now);
            }

            _timer.Start();
            RaiseTick();
            return;
        }

        if (!_settings.Enabled)
        {
            SetDisabled();
            return;
        }

        if (Phase == SessionPacerPhase.Running)
        {
            _timer.Start();
        }

        RaiseTick();
    }

    /// <summary>
    /// Supplies the next varied proxy handover boundary. A running session is shortened only when
    /// needed so an ordinary pacing sleep can contain the handover; the proxy itself is applied by
    /// the host while the browser is logged out.
    /// </summary>
    public void SetNextProxyTransition(DateTimeOffset? transitionAt)
    {
        _proxyTransitionAt = transitionAt;
        if (Phase == SessionPacerPhase.Running && transitionAt is { } target)
        {
            var alignedSleepStart = target.AddMinutes(-Math.Max(5, _settings.SleepMinMinutes));
            _runDeadline = Earliest(_runDeadline, alignedSleepStart <= _now() ? _now() : alignedSleepStart);
        }

        RaiseTick();
    }

    public void NotifyAutomationStarted() => NotifyOnlineSessionStarted();

    public void NotifyAutomationStopped() => NotifyOnlineSessionStopped();

    public void NotifyOnlineSessionStarted()
    {
        if (_automationActive && Phase == SessionPacerPhase.Running)
        {
            RaiseTick();
            return;
        }

        _automationActive = true;
        _manualSleep = false;
        var now = _now();
        UpdateRuntimeDate(now);

        if (!_settings.Enabled)
        {
            Phase = SessionPacerPhase.Disabled;
            RaiseTick();
            return;
        }

        var restriction = GetActiveRestriction(now);
        if (restriction != SessionSleepReason.None)
        {
            Phase = SessionPacerPhase.Running;
            _lastRuntimeUpdate = now;
            RequestSleep(restriction);
            return;
        }

        if (Phase == SessionPacerPhase.Paused && _pausedRunRemaining is not null)
        {
            Phase = SessionPacerPhase.Running;
            _runStartedAt = now;
            _runDeadline = Earliest(now.Add(_pausedRunRemaining.Value), GetNextRestrictionAt(now));
            _sleepStartRaised = false;
            _lastRuntimeUpdate = now;
            _timer.Start();
            Logger?.Invoke($"[pacing] session run timer resumed; next sleep in {Format(TimeUntilSleep)}.");
            RaiseTick();
            return;
        }

        StartNewRun(now);
    }

    public void NotifyOnlineSessionStopped()
    {
        if (!_automationActive)
        {
            RaiseTick();
            return;
        }

        UpdateRuntime(_now(), forcePersist: true);
        _automationActive = false;
        if (Phase == SessionPacerPhase.Running)
        {
            _pausedRunRemaining = TimeUntilSleep ?? TimeSpan.Zero;
            Phase = SessionPacerPhase.Paused;
            _runStartedAt = null;
            _runDeadline = null;
            _sleepStartRaised = false;
            _lastRuntimeUpdate = null;
            _timer.Stop();
            Logger?.Invoke($"[pacing] session run timer paused; {Format(_pausedRunRemaining)} remaining.");
        }

        RaiseTick();
    }

    public SessionPacerDailyProgress GetDailyProgress()
    {
        var now = _now();
        UpdateRuntime(now);
        var limit = !_settings.Enabled || _settings.DailyMaxHours <= 0
            ? (TimeSpan?)null
            : TimeSpan.FromSeconds(GetDailyLimitSeconds(now));
        var online = TimeSpan.FromSeconds(Math.Max(0, _runtimeSeconds));
        var left = limit is null ? (TimeSpan?)null : Positive(limit.Value - online);

        return new SessionPacerDailyProgress(
            _runtimeDate,
            online,
            left,
            limit,
            _settings.DailyMaxHours);
    }

    public void Reset()
    {
        UpdateRuntime(_now(), forcePersist: true);
        _automationActive = false;
        _manualSleep = false;
        SleepReason = SessionSleepReason.None;
        _pendingSleepReason = SessionSleepReason.None;
        _scheduleOverrideUntil = null;
        Phase = SessionPacerPhase.Disabled;
        _runStartedAt = null;
        _runDeadline = null;
        _wakeAt = null;
        _activeRunDuration = null;
        _activeSleepDuration = null;
        _pausedRunRemaining = null;
        _lastRuntimeUpdate = null;
        _sleepStartRaised = false;
        _timer.Stop();
        RaiseTick();
    }

    public void BeginSleep(bool manual = false)
    {
        var reason = manual
            ? SessionSleepReason.Manual
            : _pendingSleepReason == SessionSleepReason.None
                ? SessionSleepReason.SessionPacing
                : _pendingSleepReason;
        _pendingSleepReason = SessionSleepReason.None;

        if (!_settings.Enabled && reason == SessionSleepReason.SessionPacing)
        {
            SetDisabled();
            return;
        }

        UpdateRuntime(_now(), forcePersist: true);
        _manualSleep = manual;
        SleepReason = reason;
        var now = _now();
        if (reason is SessionSleepReason.Schedule or SessionSleepReason.DailyLimit)
        {
            _wakeAt = ResolveRestrictionWake(now);
            _activeSleepDuration = Positive(_wakeAt.Value - now);
        }
        else
        {
            var sleepMinutes = RandomMinutesInRange(_settings.SleepMinMinutes, _settings.SleepMaxMinutes);
            if (_proxyTransitionAt is { } proxyAt && proxyAt > now)
            {
                // If this sleep is close enough to the next handover, keep the browser offline until
                // after that boundary. The random sleep range still decides the actual wake time.
                var minutesToBoundary = (proxyAt - now).TotalMinutes;
                if (minutesToBoundary <= _settings.SleepMaxMinutes)
                {
                    sleepMinutes = Math.Max(sleepMinutes, minutesToBoundary);
                }
            }

            _activeSleepDuration = TimeSpan.FromMinutes(sleepMinutes);
            _wakeAt = now.Add(_activeSleepDuration.Value);
        }

        Phase = SessionPacerPhase.Sleeping;
        _runDeadline = null;
        _runStartedAt = null;
        _pausedRunRemaining = null;
        _lastRuntimeUpdate = null;
        _sleepStartRaised = false;
        _automationActive = false;
        _timer.Start();
        Logger?.Invoke($"{SleepReasonLabel(reason)} sleep starting; sleeping for {Format(TimeUntilWake)}.");
        RaiseTick();
    }

    public void WakeNow()
    {
        if (!CanWakeNow)
        {
            return;
        }

        // Waking out of a scheduled off-hours sleep is an explicit override: keep the schedule
        // restriction suppressed until it would next permit running on its own, so the bot actually
        // runs through the disallowed window instead of immediately going back to sleep.
        if (SleepReason == SessionSleepReason.Schedule)
        {
            var now = _now();
            _scheduleOverrideUntil = GetNextScheduleTransition(now, allowed: true) ?? now.AddDays(1);
            Logger?.Invoke("[pacing] manual Run now: overriding the schedule for the current off-hours window.");
        }

        CompleteSleepAndWake();
    }

    // True when a fresh online session would immediately be forced to sleep by an active restriction
    // (off-hours schedule or the daily runtime limit). Lets the caller skip the login + analyze stack and
    // go straight to sleep instead of logging in only to log out and sleep again.
    public bool ShouldSleepNow()
    {
        return _settings.Enabled
            && Phase != SessionPacerPhase.Sleeping
            && GetActiveRestriction(_now()) != SessionSleepReason.None;
    }

    // Puts the pacer straight into the currently-due restriction sleep (schedule / daily limit) without
    // going online first. Returns false when no restriction is active. Used when the user presses Login
    // during a planned off-hours / daily-limit window.
    public bool BeginScheduledSleepNow()
    {
        if (!_settings.Enabled || Phase == SessionPacerPhase.Sleeping)
        {
            return false;
        }

        var restriction = GetActiveRestriction(_now());
        if (restriction == SessionSleepReason.None)
        {
            return false;
        }

        _pendingSleepReason = restriction;
        BeginSleep(manual: false);
        return true;
    }

    public void TickForTests() => TickTimer();

    // Re-evaluates the manual-function pause immediately (host calls this when such a function starts or
    // stops). Kept separate from the 1s tick so the countdown freezes/resumes without up to a second of lag.
    public void SyncManualOperationPause()
    {
        ReconcileManualOperationPause(_now());
        RaiseTick();
    }

    // Freezes the run->sleep countdown while a scope-limited manual function runs, and resumes it with the
    // SAME remaining time afterwards (so the bot doesn't sleep the instant the function finishes). Only acts
    // on a running countdown: if nothing was counting down (idle/disabled/sleeping) it never starts one.
    private void ReconcileManualOperationPause(DateTimeOffset now)
    {
        var active = IsManualOperationActive?.Invoke() ?? false;
        if (active)
        {
            if (Phase == SessionPacerPhase.Running)
            {
                _pausedRunRemaining = TimeUntilSleep ?? TimeSpan.Zero;
                _manualOperationPaused = true;
                Phase = SessionPacerPhase.Paused;
                _runStartedAt = null;
                _runDeadline = null;
                _lastRuntimeUpdate = null;
                _sleepStartRaised = false;
                _timer.Stop();
                Logger?.Invoke($"[pacing] run timer paused for manual function; {Format(_pausedRunRemaining)} remaining.");
            }

            return;
        }

        if (!_manualOperationPaused)
        {
            return;
        }

        _manualOperationPaused = false;
        if (Phase != SessionPacerPhase.Paused || !_settings.Enabled || !_automationActive)
        {
            return;
        }

        Phase = SessionPacerPhase.Running;
        _runStartedAt = now;
        _runDeadline = Earliest(now.Add(_pausedRunRemaining ?? TimeSpan.Zero), GetNextRestrictionAt(now));
        _pausedRunRemaining = null;
        _lastRuntimeUpdate = now;
        _timer.Start();
        Logger?.Invoke($"[pacing] run timer resumed after manual function; next sleep in {Format(TimeUntilSleep)}.");
    }

    private void StartNewRun(DateTimeOffset now)
    {
        Phase = SessionPacerPhase.Running;
        SleepReason = SessionSleepReason.None;
        _runStartedAt = now;
        _activeRunDuration = TimeSpan.FromMinutes(RandomMinutesInRange(_settings.RunMinMinutes, _settings.RunMaxMinutes));
        _runDeadline = Earliest(now.Add(_activeRunDuration.Value), GetNextRestrictionAt(now));
        if (_proxyTransitionAt is { } proxyAt)
        {
            var alignedSleepStart = proxyAt.AddMinutes(-Math.Max(5, _settings.SleepMinMinutes));
            _runDeadline = Earliest(_runDeadline, alignedSleepStart <= now ? now : alignedSleepStart);
        }
        _pausedRunRemaining = null;
        _wakeAt = null;
        _sleepStartRaised = false;
        _lastRuntimeUpdate = now;
        _timer.Start();
        Logger?.Invoke($"[pacing] session run timer started; next sleep in {Format(TimeUntilSleep)}.");
        RaiseTick();
    }

    private void CompleteSleepAndWake()
    {
        _wakeAt = null;
        _manualSleep = false;
        _activeSleepDuration = null;
        SleepReason = SessionSleepReason.None;
        Phase = SessionPacerPhase.Disabled;
        _timer.Stop();
        Logger?.Invoke("Session waking - resuming.");
        WakeRequested?.Invoke(this, EventArgs.Empty);
        RaiseTick();
    }

    private void TickTimer()
    {
        var now = _now();
        UpdateRuntime(now);
        ReconcileManualOperationPause(now);

        if (!_settings.Enabled && !_manualSleep && Phase != SessionPacerPhase.Sleeping)
        {
            SetDisabled();
            return;
        }

        if (Phase == SessionPacerPhase.Running)
        {
            var restriction = GetActiveRestriction(now);
            if (restriction != SessionSleepReason.None)
            {
                RequestSleep(restriction);
            }
            else if (TimeUntilSleep <= TimeSpan.Zero)
            {
                RequestSleep(SessionSleepReason.SessionPacing);
            }
        }
        else if (Phase == SessionPacerPhase.Sleeping && TimeUntilWake <= TimeSpan.Zero)
        {
            var restriction = GetActiveRestriction(now);
            if (restriction == SessionSleepReason.None)
            {
                CompleteSleepAndWake();
            }
            else
            {
                SleepReason = restriction;
                _wakeAt = ResolveRestrictionWake(now);
            }
        }

        RaiseTick();
    }

    private void RequestSleep(SessionSleepReason reason)
    {
        if (_sleepStartRaised)
        {
            return;
        }

        _pendingSleepReason = reason;
        _sleepStartRaised = true;
        SleepStarting?.Invoke(this, EventArgs.Empty);
        RaiseTick();
    }

    private SessionSleepReason GetActiveRestriction(DateTimeOffset now)
    {
        UpdateRuntimeDate(now);

        // Expire a manual schedule override once we reach the point the schedule would allow anyway.
        if (_scheduleOverrideUntil is not null && now >= _scheduleOverrideUntil.Value)
        {
            _scheduleOverrideUntil = null;
        }

        if (_scheduleOverrideUntil is null && !IsScheduleAllowed(now))
        {
            return SessionSleepReason.Schedule;
        }

        return IsDailyLimitReached(now) ? SessionSleepReason.DailyLimit : SessionSleepReason.None;
    }

    private DateTimeOffset ResolveRestrictionWake(DateTimeOffset now)
    {
        DateTimeOffset? candidate = null;
        if (!IsScheduleAllowed(now))
        {
            candidate = GetNextScheduleTransition(now, allowed: true) ?? now.AddDays(2);
        }

        if (IsDailyLimitReached(now))
        {
            var nextMidnight = StartOfLocalDay(now).AddDays(1);
            if (!IsScheduleAllowed(nextMidnight))
            {
                nextMidnight = GetNextScheduleTransition(nextMidnight, allowed: true) ?? nextMidnight.AddDays(1);
            }

            candidate = candidate is null || candidate < nextMidnight ? nextMidnight : candidate;
        }

        return candidate ?? now.AddMinutes(Math.Max(30, _settings.SleepMinMinutes));
    }

    private DateTimeOffset? GetNextRestrictionAt(DateTimeOffset now)
    {
        DateTimeOffset? result = GetNextScheduleTransition(now, allowed: false);
        if (_settings.DailyMaxHours > 0)
        {
            var remainingSeconds = GetDailyLimitSeconds(now) - _runtimeSeconds;
            var dailyAt = now.AddSeconds(Math.Max(0, remainingSeconds));
            result = Earliest(result, dailyAt);
        }

        return result;
    }

    private bool IsScheduleAllowed(DateTimeOffset now)
    {
        var rangeStart = now.AddDays(-2);
        var state = _allowedHours.Contains(rangeStart.Hour);
        foreach (var transition in GetScheduleTransitions(rangeStart, now.AddDays(2)))
        {
            if (transition.At <= rangeStart)
            {
                continue;
            }

            if (transition.At > now)
            {
                break;
            }

            state = transition.Allowed;
        }

        return state;
    }

    private DateTimeOffset? GetNextScheduleTransition(DateTimeOffset now, bool allowed)
    {
        return GetScheduleTransitions(now, now.AddDays(3))
            .FirstOrDefault(transition => transition.At > now && transition.Allowed == allowed)
            ?.At;
    }

    private List<ScheduleTransition> GetScheduleTransitions(DateTimeOffset from, DateTimeOffset to)
    {
        var transitions = new List<ScheduleTransition>();
        var day = DateOnly.FromDateTime(from.DateTime).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(to.DateTime).AddDays(1);
        for (; day <= lastDay; day = day.AddDays(1))
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var previousHour = (hour + 23) % 24;
                var allowed = _allowedHours.Contains(hour);
                if (allowed == _allowedHours.Contains(previousHour))
                {
                    continue;
                }

                // Jitter each on/off boundary by ±HoursVariationPercent of an hour so the bot doesn't
                // start/stop at the exact same clock time every day (more human). Deterministic per
                // day+hour so the boundary stays stable within a day (this runs repeatedly), but varies
                // across days. 0% => exact boundaries.
                var boundary = WallTime(day, hour, from.Offset);
                var offsetMinutes = _settings.HoursVariationPercent <= 0
                    ? 0
                    : DeterministicHourFraction(day, hour) * _settings.HoursVariationPercent / 100.0 * 60;
                transitions.Add(new ScheduleTransition(boundary.AddMinutes(offsetMinutes), allowed));
            }
        }

        return transitions.OrderBy(transition => transition.At).ToList();
    }

    private bool IsDailyLimitReached(DateTimeOffset now)
    {
        return _settings.DailyMaxHours > 0 && _runtimeSeconds >= GetDailyLimitSeconds(now);
    }

    private double GetDailyLimitSeconds(DateTimeOffset now)
    {
        if (_settings.DailyMaxHours <= 0)
        {
            return double.MaxValue;
        }

        var date = DateOnly.FromDateTime(now.DateTime);
        var baseMinutes = _settings.DailyMaxHours * 60.0;
        // Daily-max uses its OWN variation (DailyMaxVariationPercent), not the run/sleep/schedule
        // VariationPercent. A per-day fraction keyed on the calendar day number (splitmix64) so the limit
        // is stable within a day but genuinely spreads across consecutive days — the old date-string FNV
        // hash clustered near zero for runs of adjacent days, which made the cap look like it never varied.
        var spread = baseMinutes * _settings.DailyMaxVariationPercent / 100.0;
        var variedMinutes = baseMinutes + (DeterministicDailyFraction(date) * spread);
        return Math.Max(1, variedMinutes) * 60;
    }

    private void UpdateRuntime(DateTimeOffset now, bool forcePersist = false)
    {
        UpdateRuntimeDate(now);
        if (_automationActive && _lastRuntimeUpdate is not null)
        {
            _runtimeSeconds += Math.Max(0, (now - _lastRuntimeUpdate.Value).TotalSeconds);
        }

        _lastRuntimeUpdate = _automationActive ? now : null;
        if (forcePersist || _lastRuntimePersist is null || now - _lastRuntimePersist >= TimeSpan.FromMinutes(1))
        {
            _lastRuntimePersist = now;
            RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateRuntimeDate(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        if (_runtimeDate == today)
        {
            return;
        }

        _runtimeDate = today;
        _runtimeSeconds = 0;
        _lastRuntimePersist = null;
        RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDisabled()
    {
        Phase = SessionPacerPhase.Disabled;
        SleepReason = SessionSleepReason.None;
        _runStartedAt = null;
        _runDeadline = null;
        _wakeAt = null;
        _activeRunDuration = null;
        _pausedRunRemaining = null;
        _lastRuntimeUpdate = null;
        _sleepStartRaised = false;
        _timer.Stop();
        RaiseTick();
    }

    // Fresh random pick in [min, max] minutes each time a run/sleep actually starts. Safe to
    // randomize here because the result is pinned to the wake/run deadline once and never recomputed
    // during the phase. Schedule boundaries and the daily limit deliberately keep the deterministic
    // fractions since those are recomputed repeatedly and must stay stable.
    private static double RandomMinutesInRange(int minMinutes, int maxMinutes)
    {
        var min = Math.Max(1, minMinutes);
        var max = Math.Max(min, maxMinutes);
        return min + (Random.Shared.NextDouble() * (max - min));
    }

    // Well-distributed signed fraction in [-1, 1] for a calendar day. Uses the splitmix64 finalizer on the
    // day number, which fully avalanches, so consecutive days produce uncorrelated values (unlike hashing
    // the date string, where adjacent days share a long prefix and clustered near zero). Deterministic, so
    // the daily-max limit stays the same across app restarts within a day.
    private static double DeterministicDailyFraction(DateOnly date)
    {
        var z = unchecked((ulong)date.DayNumber + 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z / (double)ulong.MaxValue * 2) - 1;
    }

    // Signed fraction in [-1, 1] for a specific day+hour boundary, used to jitter the allowed-hours
    // schedule. Same splitmix64 finalizer as above but seeded per (day, hour) so each boundary gets its
    // own stable offset within the day; distinct hours on the same day get uncorrelated values.
    private static double DeterministicHourFraction(DateOnly date, int hour)
    {
        var z = unchecked(((ulong)date.DayNumber * 24UL) + (ulong)hour + 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z / (double)ulong.MaxValue * 2) - 1;
    }

    // Start of the wall-clock day in the clock source's own offset (not the machine timezone), so the
    // pacer stays consistent with the injected clock. With the default clock (DateTimeOffset.Now) the
    // offset is the local one, so production behavior is unchanged.
    private static DateTimeOffset StartOfLocalDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.DateTime.Date, value.Offset);
    }

    // An hour boundary on a given day, expressed in the supplied offset (the clock source's offset).
    private static DateTimeOffset WallTime(DateOnly date, int hour, TimeSpan offset)
    {
        return new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), offset);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first <= second ? first : second;
    }

    private static SessionPacerSettings Normalize(SessionPacerSettings settings)
    {
        return settings with
        {
            RunMinMinutes = Math.Max(1, settings.RunMinMinutes),
            RunMaxMinutes = Math.Max(Math.Max(1, settings.RunMinMinutes), settings.RunMaxMinutes),
            SleepMinMinutes = Math.Max(5, settings.SleepMinMinutes),
            SleepMaxMinutes = Math.Max(Math.Max(5, settings.SleepMinMinutes), settings.SleepMaxMinutes),
            DailyMaxHours = Math.Clamp(settings.DailyMaxHours, 0, 24),
            DailyMaxVariationPercent = Math.Clamp(settings.DailyMaxVariationPercent, 0, 50),
            // Capped at 49% so a ±jitter can never push adjacent hour boundaries past each other.
            HoursVariationPercent = Math.Clamp(settings.HoursVariationPercent, 0, 49),
            RuntimeSeconds = Math.Max(0, settings.RuntimeSeconds),
        };
    }

    private static string SleepReasonLabel(SessionSleepReason reason) => reason switch
    {
        SessionSleepReason.Schedule => "Scheduled off-hours",
        SessionSleepReason.DailyLimit => "Daily runtime limit",
        SessionSleepReason.Manual => "Session sleep (manual)",
        _ => "Session",
    };

    private void RaiseTick()
    {
        Tick?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Positive(TimeSpan value) => value <= TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string Format(TimeSpan? value)
    {
        var span = value ?? TimeSpan.Zero;
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string FormatDuration(TimeSpan? value) => Format(value);

    private sealed record ScheduleTransition(DateTimeOffset At, bool Allowed);
}
