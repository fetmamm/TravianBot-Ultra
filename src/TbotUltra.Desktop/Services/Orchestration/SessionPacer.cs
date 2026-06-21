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
    int MaxRunMinutes,
    int SleepMinutes,
    int VariationPercent,
    IReadOnlyList<int>? AllowedHours = null,
    int DailyMaxHours = 0,
    DateOnly? RuntimeDate = null,
    double RuntimeSeconds = 0);

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
        PacingDefaults.SessionPacingMaxRunMinutes,
        PacingDefaults.SessionPacingSleepMinutes,
        PacingDefaults.SessionPacingVariationPercent,
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
    private bool _runtimeLoaded;
    private SessionSleepReason _pendingSleepReason;

    public SessionPacer(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _runtimeDate = DateOnly.FromDateTime(_now().DateTime);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickTimer();
    }

    public Action<string>? Logger { get; set; }
    public SessionPacerPhase Phase { get; private set; } = SessionPacerPhase.Disabled;
    public SessionSleepReason SleepReason { get; private set; }
    public bool CanWakeNow => Phase == SessionPacerPhase.Sleeping
        && SleepReason is SessionSleepReason.SessionPacing or SessionSleepReason.Manual;
    public TimeSpan? TimeUntilSleep => _runDeadline is null ? null : Positive(_runDeadline.Value - _now());
    public TimeSpan? TimeUntilWake => _wakeAt is null ? null : Positive(_wakeAt.Value - _now());
    public TimeSpan? ActiveRunDuration => _activeRunDuration;
    public TimeSpan? ActiveSleepDuration => _activeSleepDuration;
    public SessionPacerRuntimeState RuntimeState => new(_runtimeDate, _runtimeSeconds);
    public string StatusText => Phase switch
    {
        SessionPacerPhase.Running => $"Next sleep in: {Format(TimeUntilSleep)}",
        SessionPacerPhase.Paused => $"Paused - next sleep in: {Format(_pausedRunRemaining)}",
        SessionPacerPhase.Sleeping when SleepReason == SessionSleepReason.Schedule => $"Scheduled off - {Format(TimeUntilWake)}",
        SessionPacerPhase.Sleeping when SleepReason == SessionSleepReason.DailyLimit => $"Daily limit - {Format(TimeUntilWake)}",
        SessionPacerPhase.Sleeping => $"Sleeping - {Format(TimeUntilWake)}",
        _ => _automationActive ? "Session pacing off" : "Session pacing",
    };

    public event EventHandler? SleepStarting;
    public event EventHandler? WakeRequested;
    public event EventHandler? RuntimeStateChanged;
    public event EventHandler? Tick;

    public void Configure(SessionPacerSettings settings)
    {
        var previousDailyMaxHours = _settings.DailyMaxHours;
        _settings = Normalize(settings);
        _allowedHours = (_settings.AllowedHours ?? Enumerable.Range(0, 24))
            .Where(hour => hour is >= 0 and <= 23)
            .ToHashSet();

        var now = _now();
        var today = DateOnly.FromDateTime(now.DateTime);
        if (!_runtimeLoaded)
        {
            _runtimeDate = today;
            _runtimeSeconds = _settings.RuntimeDate == today ? _settings.RuntimeSeconds : 0;
            _runtimeLoaded = true;
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
            _activeSleepDuration = TimeSpan.FromMinutes(ApplyVariation(_settings.SleepMinutes));
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

        CompleteSleepAndWake();
    }

    public void TickForTests() => TickTimer();

    private void StartNewRun(DateTimeOffset now)
    {
        Phase = SessionPacerPhase.Running;
        SleepReason = SessionSleepReason.None;
        _runStartedAt = now;
        _activeRunDuration = TimeSpan.FromMinutes(ApplyVariation(_settings.MaxRunMinutes));
        _runDeadline = Earliest(now.Add(_activeRunDuration.Value), GetNextRestrictionAt(now));
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
        if (!IsScheduleAllowed(now))
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

        return candidate ?? now.AddMinutes(Math.Max(30, _settings.SleepMinutes));
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

                var boundary = WallTime(day, hour, from.Offset);
                var offsetMinutes = DeterministicSignedFraction($"schedule:{day:yyyyMMdd}:{hour}")
                    * Math.Min(_settings.VariationPercent, 49) / 100.0 * 60;
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
        var spread = baseMinutes * _settings.VariationPercent / 100.0;
        var variedMinutes = baseMinutes + (DeterministicSignedFraction($"daily:{date:yyyyMMdd}") * spread);
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

    private double ApplyVariation(int minutes)
    {
        var baseMinutes = Math.Max(1, minutes);
        var variation = Math.Clamp(_settings.VariationPercent, 0, 100);
        if (variation <= 0)
        {
            return baseMinutes;
        }

        // Pick a fresh random offset each time a sleep/run actually starts so the duration genuinely
        // varies across base +/- variation% (e.g. 40 min @ 40% -> anywhere in 24..56 min). The previous
        // deterministic per-hour value could land on almost no change, which made the variation look
        // broken. Randomizing here is safe because the result is pinned to the wake/run deadline once and
        // never recomputed during the phase. Schedule boundaries and the daily limit deliberately keep
        // DeterministicSignedFraction since those are recomputed repeatedly and must stay stable.
        var fraction = (Random.Shared.NextDouble() * 2) - 1; // [-1, 1]
        var spread = baseMinutes * variation / 100.0;
        return Math.Max(1.0 / 60.0, baseMinutes + (fraction * spread));
    }

    private static double DeterministicSignedFraction(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }

        return (hash / (double)uint.MaxValue * 2) - 1;
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
            MaxRunMinutes = Math.Max(1, settings.MaxRunMinutes),
            SleepMinutes = Math.Max(30, settings.SleepMinutes),
            VariationPercent = Math.Clamp(settings.VariationPercent, 0, 100),
            DailyMaxHours = Math.Clamp(settings.DailyMaxHours, 0, 24),
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
