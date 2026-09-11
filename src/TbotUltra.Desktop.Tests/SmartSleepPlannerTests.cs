using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SmartSleepPlannerTests
{
    private static readonly SmartSleepSettings Settings = new(true, 20, 5, 10, 30, 60);

    [Fact]
    public void TrustedDeadline_UsesConfigurableWakeWindow()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var deadline = now.AddHours(1);

        var early = SmartSleepPlanner.Plan(now, deadline, Settings, (min, _) => min);
        var late = SmartSleepPlanner.Plan(now, deadline, Settings, (_, max) => max - 1);

        Assert.Equal(deadline.AddMinutes(-5), early.WakeAtUtc);
        Assert.Equal(deadline.AddMinutes(10), late.WakeAtUtc);
        Assert.True(early.ShouldSleep);
        Assert.True(late.ShouldSleep);
    }

    [Fact]
    public void OpportunityShorterThanMinimum_StaysOnline()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

        var plan = SmartSleepPlanner.Plan(now, now.AddMinutes(22), Settings, (min, _) => min);

        Assert.False(plan.ShouldSleep);
    }

    [Fact]
    public void MissingDeadline_UsesFallbackRange()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

        var plan = SmartSleepPlanner.Plan(now, null, Settings, (_, max) => max - 1);

        Assert.True(plan.ShouldSleep);
        Assert.True(plan.UsesFallback);
        Assert.Equal(now.AddMinutes(60), plan.WakeAtUtc);
    }
}
