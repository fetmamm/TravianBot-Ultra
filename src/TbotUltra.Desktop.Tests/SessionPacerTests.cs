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
        Assert.Equal(10, PacingDefaults.SessionPacingDailyMaxVariationPercent);
    }

    [Fact]
    public void SleepingStatusText_ShowsResumeCountdown()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));

        pacer.BeginSleep();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Matches(@"^Sleeping: \d{2}:\d{2}:\d{2}$", pacer.StatusText);
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
        // A scheduled off-hours sleep is wakeable: "Run now" overrides the schedule for that window.
        Assert.True(pacer.CanWakeNow);
        Assert.InRange(pacer.TimeUntilWake!.Value.TotalMinutes, 164, 166);
    }

    [Fact]
    public void WakeNow_DuringScheduleSleep_OverridesScheduleAndKeepsRunning()
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

        pacer.WakeNow();
        // The host resumes automation when the pacer raises WakeRequested.
        pacer.NotifyAutomationStarted();

        // Hour 2 is still disallowed, but the manual override runs through it instead of re-sleeping.
        Assert.Equal(SessionPacerPhase.Running, pacer.Phase);
        Assert.Equal(SessionSleepReason.None, pacer.SleepReason);
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
            RuntimeSeconds: TimeSpan.FromHours(12).TotalSeconds,
            DailyMaxVariationPercent: 0));
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
            RuntimeSeconds: TimeSpan.FromHours(3).TotalSeconds,
            DailyMaxVariationPercent: 0));

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
    public void Configure_WithReloadRuntime_ReplacesPreviousAccountRuntime()
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
            RuntimeSeconds: TimeSpan.FromHours(10).TotalSeconds));

        pacer.Configure(
            new SessionPacerSettings(
                true,
                120,
                30,
                0,
                Enumerable.Range(0, 24).ToArray(),
                DailyMaxHours: 12,
                RuntimeDate: new DateOnly(2026, 6, 14),
                RuntimeSeconds: TimeSpan.FromHours(2).TotalSeconds),
            reloadRuntime: true);

        Assert.Equal(TimeSpan.FromHours(2).TotalSeconds, pacer.RuntimeState.RuntimeSeconds);
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
            RuntimeSeconds: TimeSpan.FromHours(1).TotalSeconds,
            DailyMaxVariationPercent: 0));
        pacer.SleepStarting += (_, _) => pacer.BeginSleep();
        var wakeRequested = false;
        pacer.WakeRequested += (_, _) => wakeRequested = true;
        pacer.NotifyAutomationStarted();

        now = now.AddMinutes(1);
        pacer.TickForTests();

        Assert.True(wakeRequested);
        Assert.Equal(0, pacer.RuntimeState.RuntimeSeconds);
    }

    [Fact]
    public void DailyLimit_VariesAcrossConsecutiveDays()
    {
        var caps = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            var day = new DateOnly(2026, 1, 1).AddDays(i);
            var now = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            var pacer = new SessionPacer(() => now);
            pacer.Configure(new SessionPacerSettings(
                true,
                120,
                30,
                0,
                Enumerable.Range(0, 24).ToArray(),
                DailyMaxHours: 16,
                DailyMaxVariationPercent: 40));
            caps.Add(pacer.GetDailyProgress().Limit!.Value.TotalHours);
        }

        // 40% variation on 16h must genuinely spread across days, not cluster near 16h (the old
        // date-string hash did, which made the cap look constant).
        Assert.True(caps.Max() - caps.Min() > 6, $"range={caps.Max() - caps.Min():F2}");
        Assert.True(caps.Count(h => Math.Abs(h - 16) > 1) >= 20);
    }

    [Fact]
    public void DailyLimit_IgnoresRunSleepVariationPercent()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

        TimeSpan LimitWithRunSleepVariation(int runSleepVariationPercent)
        {
            var pacer = new SessionPacer(() => now);
            pacer.Configure(new SessionPacerSettings(
                true,
                120,
                30,
                runSleepVariationPercent,
                Enumerable.Range(0, 24).ToArray(),
                DailyMaxHours: 16,
                DailyMaxVariationPercent: 20));
            return pacer.GetDailyProgress().Limit!.Value;
        }

        // The run/sleep "Variation" must not move the daily-max limit — only DailyMaxVariationPercent does.
        Assert.Equal(LimitWithRunSleepVariation(0), LimitWithRunSleepVariation(100));
    }
}
