using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SessionPacerTests
{
    [Fact]
    public void SleepingStatusText_DoesNotShowResumeCountdown()
    {
        var pacer = new SessionPacer();
        pacer.Configure(new SessionPacerSettings(true, 120, 30, 0));

        pacer.BeginSleep();

        Assert.Equal(SessionPacerPhase.Sleeping, pacer.Phase);
        Assert.Equal("Sleeping", pacer.StatusText);
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
}
