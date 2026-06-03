using System.Windows.Threading;

namespace TbotUltra.Desktop.Services.Orchestration;

public enum SessionPacerPhase
{
    Disabled,
    Running,
    Sleeping,
}

public sealed record SessionPacerSettings(
    bool Enabled,
    int MaxRunMinutes,
    int SleepMinutes,
    int VariationPercent);

public sealed class SessionPacer
{
    private readonly DispatcherTimer _timer;
    private SessionPacerSettings _settings = new(true, 120, 60, 15);
    private DateTimeOffset? _runStartedAt;
    private DateTimeOffset? _runDeadline;
    private DateTimeOffset? _wakeAt;
    private bool _sleepStartRaised;
    private bool _automationActive;
    private bool _manualSleep;

    public SessionPacer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickTimer();
    }

    public Action<string>? Logger { get; set; }
    public SessionPacerPhase Phase { get; private set; } = SessionPacerPhase.Disabled;
    public TimeSpan? TimeUntilSleep => _runDeadline is null ? null : Positive(_runDeadline.Value - DateTimeOffset.Now);
    public TimeSpan? TimeUntilWake => _wakeAt is null ? null : Positive(_wakeAt.Value - DateTimeOffset.Now);
    public string StatusText => Phase switch
    {
        SessionPacerPhase.Running => $"Next sleep in: {Format(TimeUntilSleep)}",
        SessionPacerPhase.Sleeping => $"Sleeping - resumes in {Format(TimeUntilWake)}",
        // Idle before the loop runs: stay neutral ("Session pacing"). Only show "off" once
        // automation is actually running but pacing is disabled in settings.
        _ => _automationActive ? "Session pacing off" : "Session pacing",
    };

    public event EventHandler? SleepStarting;
    public event EventHandler? WakeRequested;
    public event EventHandler? Tick;

    public void Configure(SessionPacerSettings settings)
    {
        _settings = Normalize(settings);
        if (!_settings.Enabled)
        {
            Phase = SessionPacerPhase.Disabled;
            _runStartedAt = null;
            _runDeadline = null;
            _wakeAt = null;
            _sleepStartRaised = false;
            _timer.Stop();
            RaiseTick();
            return;
        }

        if (Phase is SessionPacerPhase.Running or SessionPacerPhase.Sleeping)
        {
            _timer.Start();
        }

        RaiseTick();
    }

    public void NotifyAutomationStarted()
    {
        _automationActive = true;
        _manualSleep = false;
        if (!_settings.Enabled)
        {
            Phase = SessionPacerPhase.Disabled;
            RaiseTick();
            return;
        }

        Phase = SessionPacerPhase.Running;
        _runStartedAt = DateTimeOffset.Now;
        _runDeadline = _runStartedAt.Value.AddMinutes(ApplyVariation(_settings.MaxRunMinutes));
        _wakeAt = null;
        _sleepStartRaised = false;
        _timer.Start();
        Logger?.Invoke($"[pacing] session run timer started; next sleep in {Format(TimeUntilSleep)}.");
        RaiseTick();
    }

    public void NotifyAutomationStopped()
    {
        _automationActive = false;
        if (Phase == SessionPacerPhase.Running)
        {
            Phase = SessionPacerPhase.Disabled;
            _runStartedAt = null;
            _runDeadline = null;
            _sleepStartRaised = false;
            _timer.Stop();
        }

        RaiseTick();
    }

    // manual=true is a user-triggered "Sleep now": it sleeps even when session pacing is disabled,
    // using the configured sleep duration, and auto-wakes the same way the timed cycle does.
    public void BeginSleep(bool manual = false)
    {
        if (!_settings.Enabled && !manual)
        {
            Phase = SessionPacerPhase.Disabled;
            RaiseTick();
            return;
        }

        _manualSleep = manual;
        var ranFor = _runStartedAt is null ? TimeSpan.Zero : DateTimeOffset.Now - _runStartedAt.Value;
        _wakeAt = DateTimeOffset.Now.AddMinutes(ApplyVariation(_settings.SleepMinutes));
        Phase = SessionPacerPhase.Sleeping;
        _runDeadline = null;
        _runStartedAt = null;
        _sleepStartRaised = false;
        _automationActive = false;
        _timer.Start();
        Logger?.Invoke($"Session sleep starting{(manual ? " (manual)" : string.Empty)}; sleeping for {Format(TimeUntilWake)}.");
        RaiseTick();
    }

    public void WakeNow()
    {
        if (Phase != SessionPacerPhase.Sleeping)
        {
            return;
        }

        CompleteSleepAndWake();
    }

    // One-shot wake: leave the Sleeping phase and stop the timer before raising WakeRequested so the
    // handler's login/resume runs exactly once (the timer-driven wake would otherwise refire each tick).
    private void CompleteSleepAndWake()
    {
        _wakeAt = null;
        _manualSleep = false;
        Phase = SessionPacerPhase.Disabled;
        _timer.Stop();
        Logger?.Invoke("Session waking - resuming.");
        WakeRequested?.Invoke(this, EventArgs.Empty);
        RaiseTick();
    }

    private void TickTimer()
    {
        // A manual "Sleep now" keeps the countdown alive even when session pacing is turned off.
        if (!_settings.Enabled && !_manualSleep)
        {
            Configure(_settings with { Enabled = false });
            return;
        }

        if (Phase == SessionPacerPhase.Running && TimeUntilSleep <= TimeSpan.Zero && !_sleepStartRaised)
        {
            _sleepStartRaised = true;
            SleepStarting?.Invoke(this, EventArgs.Empty);
        }
        else if (Phase == SessionPacerPhase.Sleeping && TimeUntilWake <= TimeSpan.Zero)
        {
            CompleteSleepAndWake();
        }

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

        var spread = baseMinutes * variation / 100.0;
        return Math.Max(1.0 / 60.0, baseMinutes + ((Random.Shared.NextDouble() * 2 - 1) * spread));
    }

    private void RaiseTick()
    {
        Tick?.Invoke(this, EventArgs.Empty);
    }

    private static SessionPacerSettings Normalize(SessionPacerSettings settings)
    {
        return settings with
        {
            MaxRunMinutes = Math.Max(1, settings.MaxRunMinutes),
            SleepMinutes = Math.Max(1, settings.SleepMinutes),
            VariationPercent = Math.Clamp(settings.VariationPercent, 0, 100),
        };
    }

    private static TimeSpan Positive(TimeSpan value) => value <= TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string Format(TimeSpan? value)
    {
        var span = value ?? TimeSpan.Zero;
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
