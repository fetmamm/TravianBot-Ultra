using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationSessionRuntimeTests
{
    [Fact]
    public void KeepAlive_ReenabledStartsAFreshFullInterval()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var runtime = new AutomationSessionRuntime(time, (min, _) => min);

        Assert.Equal(
            KeepAlivePlan.Disabled,
            runtime.PlanKeepAlive(false, 5, 10, false, false, true, null));
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(
            KeepAlivePlan.Scheduled,
            runtime.PlanKeepAlive(true, 5, 10, false, false, true, null));
        Assert.Equal(time.GetUtcNow().AddMinutes(5), runtime.NextKeepAliveAtUtc);
    }

    [Fact]
    public void KeepAlive_UsesImminentQueueDeadlineInsteadOfRefreshing()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var runtime = new AutomationSessionRuntime(time, (min, _) => min);
        runtime.RecordBrowserActivity(true, 1, 1);
        time.Advance(TimeSpan.FromMinutes(1));
        var pendingAt = time.GetUtcNow().AddSeconds(20);

        var plan = runtime.PlanKeepAlive(true, 1, 1, false, false, true, pendingAt);

        Assert.Equal(KeepAlivePlan.SkipImminentWork, plan);
        Assert.Equal(pendingAt.AddSeconds(5), runtime.NextKeepAliveAtUtc);
    }

    [Fact]
    public void InboxCheck_IsThrottledByTheOwnedRuntimeDeadline()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var runtime = new AutomationSessionRuntime(time);

        Assert.True(runtime.ShouldCheckInbox(true, TimeSpan.FromMinutes(2)));
        Assert.False(runtime.ShouldCheckInbox(true, TimeSpan.FromMinutes(2)));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(runtime.ShouldCheckInbox(true, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void GoldClub_TrueBecomesAuthoritativeForTheAccount()
    {
        var runtime = new AutomationSessionRuntime(
            new MutableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)));

        Assert.True(runtime.PlanGoldClubCheck("a", false, TimeSpan.FromMinutes(10)).ShouldRefresh);
        runtime.ApplyGoldClubStatus(true);

        Assert.Equal(
            new GoldClubCheckPlan(true, false),
            runtime.PlanGoldClubCheck("a", false, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void SmartSleepBlockerDiagnostic_IsRateLimitedPerTaskAndVillage()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var runtime = new AutomationSessionRuntime(time);
        const string key = "smart-sleep:blocker:TroopTraining:build_troops:xy:112|6";

        Assert.True(runtime.ShouldPublishVerbose(key, TimeSpan.FromMinutes(5)));
        Assert.False(runtime.ShouldPublishVerbose(key, TimeSpan.FromMinutes(5)));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(runtime.ShouldPublishVerbose(key, TimeSpan.FromMinutes(5)));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
