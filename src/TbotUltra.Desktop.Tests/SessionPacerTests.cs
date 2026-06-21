using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SessionPacerTests
{
    [Fact]
    public void PacingDefaults_UseConservativeSessionDefaults()
    {
        Assert.True(PacingDefaults.SessionPacingEnabled);
        Assert.Equal(90, PacingDefaults.SessionPacingMaxRunMinutes);
        Assert.Equal(45, PacingDefaults.SessionPacingSleepMinutes);
        Assert.Equal(40, PacingDefaults.SessionPacingVariationPercent);
        Assert.Equal(18, PacingDefaults.SessionPacingDailyMaxHours);
    }

    [Fact]
    public void SleepingStatusText_ShowsResumeCountdown()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));

        pacer.BeginSleep();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Matches(@"^Sleeping - \d{2}:\d{2}:\d{2}$", pacer.StatusText);
    }

    [Fact]
    public void Configure_DoesNotChangeActiveRunDeadline()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));
        pacer.NotifyAutomationStarted();
        var before = pacer.TimeUntilSleep!.Value;

        pacer.Configure(new SessionPacerSettings(true, 60, 30, 0));
        var after = pacer.TimeUntilSleep!.Value;

        Assert.InRange(Math.Abs((before - after).TotalSeconds), 0, 2);
    }

    [Fact]
    public void BeginSleep_UsesLatestSleepSettingsAndClampsMinimum()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 10, 0));

        pacer.BeginSleep();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.NotNull(pacer.ActiveSleepDuration);
        Assert.True(pacer.ActiveSleepDuration.Value >= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Configure_WhileSleepingDoesNotEndSleep()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));
        pacer.BeginSleep();

        pacer.Configure(new SessionPacerSettings(false, 120, 30, 0));

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
    }

    [Fact]
    public void PauseAndResume_PreservesRemainingRunTime()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));
        pacer.NotifyAutomationStarted();
        var beforePause = pacer.TimeUntilSleep!.Value;

        pacer.NotifyAutomationStopped();
        Assert.Equal(SessionPacerPhase.Paused, pacer.Phase);

        pacer.NotifyAutomationStarted();
        var afterResume = pacer.TimeUntilSleep!.Value;

        Assert.Equal(SessionPacerPhase.Running, pacer.Phase);
        Assert.InRange(Math.Abs((beforePause - afterResume).TotalSeconds), 0, 2);
    }

    [Fact]
    public void Reset_AfterPause_StartsANewRun()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));
        pacer.NotifyAutomationStarted();
        pacer.NotifyAutomationStopped();

        pacer.Reset();
        pacer.NotifyAutomationStarted();

        Assert.Equal(SessionPacerPhase.Running, pacer.Phase);
        Assert.InRange(pacer.TimeUntilSleep!.Value.TotalMinutes, 119, 120);
    }

    [Fact]
    public void DisabledHour_RequestsScheduleSleepUntilNextAllowedHour()
    {
        var now = new DateTimeOffset(2026, 6, 14, 2, 15, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).Except([2, 3, 4]).ToArray()));
        pacer.SleepStarting += (_, _) => pacer.BeginSleep();

        pacer.NotifyAutomationStarted();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Equal(SessionSleepReason.Schedule, pacer.SleepReason);
        Assert.False(pacer.CanWakeNow);
        Assert.InRange(pacer.TimeUntilWake!.Value.TotalMinutes, 164, 166);
    }

    [Fact]
    public void ScheduleBoundary_ShortensNormalSessionRun()
    {
        var now = new DateTimeOffset(2026, 6, 14, 1, 30, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).Except([2]).ToArray()));

        pacer.NotifyAutomationStarted();

        Assert.InRange(pacer.TimeUntilSleep!.Value.TotalMinutes, 29, 31);
    }

    [Fact]
    public void DailyRuntimeFromCurrentDate_TriggersDailyLimit()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).ToArray(),
            DailyMaxHours: 12,
            RuntimeDate: new DateOnly(2026, 6, 14),
            RuntimeSeconds: TimeSpan.FromHours(12).TotalSeconds));
        pacer.SleepStarting += (_, _) => pacer.BeginSleep();

        pacer.NotifyAutomationStarted();

        Assert.Equal(SessionSleepReason.DailyLimit, pacer.SleepReason);
        Assert.InRange(pacer.TimeUntilWake!.Value.TotalHours, 11.9, 12.1);
    }

    [Fact]
    public void DailyProgress_ReportsOnlineLeftAndActualLimit()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).ToArray(),
            DailyMaxHours: 12,
            RuntimeDate: new DateOnly(2026, 6, 14),
            RuntimeSeconds: TimeSpan.FromHours(3).TotalSeconds));

        var progress = pacer.GetDailyProgress();

        Assert.Equal(TimeSpan.FromHours(3), progress.OnlineToday);
        Assert.Equal(TimeSpan.FromHours(9), progress.TimeLeft);
        Assert.Equal(TimeSpan.FromHours(12), progress.Limit);
        Assert.Equal(12, progress.ConfiguredDailyMaxHours);
    }

    [Fact]
    public void OnlineRuntime_StopsWhenSessionStops()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).ToArray(),
            DailyMaxHours: 12));

        pacer.NotifyOnlineSessionStarted();
        now = now.AddMinutes(10);
        _ = pacer.GetDailyProgress();
        pacer.NotifyOnlineSessionStopped();
        now = now.AddMinutes(10);

        var progress = pacer.GetDailyProgress();

        Assert.Equal(TimeSpan.FromMinutes(10), progress.OnlineToday);
    }

    [Fact]
    public void RuntimeFromPreviousDate_IsDiscarded()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).ToArray(),
            DailyMaxHours: 12,
            RuntimeDate: new DateOnly(2026, 6, 13),
            RuntimeSeconds: TimeSpan.FromHours(12).TotalSeconds));

        pacer.NotifyAutomationStarted();

        Assert.Equal(SessionPacerPhase.Running, pacer.Phase);
        Assert.Equal(0, pacer.RuntimeState.RuntimeSeconds);
    }

    [Fact]
    public void Midnight_ResetsDailyRuntimeAndWakes()
    {
        var now = new DateTimeOffset(2026, 6, 14, 23, 59, 0, TimeSpan.Zero);
        var pacer = new SessionPacer(() => now);
        pacer.Configure(new SessionPacerSettings(
            true,
            120,
            30,
            0,
            Enumerable.Range(0, 24).ToArray(),
            DailyMaxHours: 1,
            RuntimeDate: new DateOnly(2026, 6, 14),
            RuntimeSeconds: TimeSpan.FromHours(1).TotalSeconds));
        pacer.SleepStarting += (_, _) => pacer.BeginSleep();
        var wakeRequested = false;
        pacer.WakeRequested += (_, _) => wakeRequested = true;
        pacer.NotifyAutomationStarted();

        now = now.AddMinutes(1);
        pacer.TickForTests();

        Assert.True(wakeRequested);
        Assert.Equal(0, pacer.RuntimeState.RuntimeSeconds);
    }
}
