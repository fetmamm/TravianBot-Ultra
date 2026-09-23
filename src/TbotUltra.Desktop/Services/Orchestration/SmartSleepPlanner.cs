using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services.Orchestration;

public sealed record SmartSleepSettings(
    bool Enabled,
    int MinimumOpportunityMinutes,
    int WakeBeforeMinutes,
    int WakeAfterMinutes,
    int FallbackMinMinutes,
    int FallbackMaxMinutes);

public enum SmartSleepDecision
{
    Disabled,
    Sleep,
    DeadlineCoalesced,
    OpportunityTooShort,
}

public readonly record struct SmartSleepPlan(
    bool ShouldSleep,
    DateTimeOffset? WakeAtUtc,
    bool UsesFallback,
    SmartSleepDecision Decision,
    int CoalescingMinutes = 0);

public static class SmartSleepPlanner
{
    public static SmartSleepPlan Plan(
        DateTimeOffset nowUtc,
        DateTimeOffset? trustedDeadlineUtc,
        SmartSleepSettings settings,
        Func<int, int, int>? nextRandom = null)
    {
        if (!settings.Enabled)
        {
            return new SmartSleepPlan(false, null, false, SmartSleepDecision.Disabled);
        }

        var random = nextRandom ?? Random.Shared.Next;
        var usesFallback = trustedDeadlineUtc is null;
        var coalescingMinutes = 0;
        if (trustedDeadlineUtc is { } trustedDeadline)
        {
            coalescingMinutes = random(
                PacingDefaults.SmartSleepDeadlineCoalescingMinMinutes,
                PacingDefaults.SmartSleepDeadlineCoalescingMaxMinutes + 1);
            if (trustedDeadline - nowUtc <= TimeSpan.FromMinutes(coalescingMinutes))
            {
                return new SmartSleepPlan(
                    false,
                    trustedDeadline,
                    false,
                    SmartSleepDecision.DeadlineCoalesced,
                    coalescingMinutes);
            }
        }

        DateTimeOffset wakeAt;
        if (usesFallback)
        {
            var min = Math.Max(1, settings.FallbackMinMinutes);
            var max = Math.Max(min, settings.FallbackMaxMinutes);
            wakeAt = nowUtc.AddMinutes(random(min, max + 1));
        }
        else if (trustedDeadlineUtc is { } deadlineUtc)
        {
            var before = Math.Max(0, settings.WakeBeforeMinutes);
            var after = Math.Max(0, settings.WakeAfterMinutes);
            var offsetMinutes = random(-before, after + 1);
            wakeAt = deadlineUtc.AddMinutes(offsetMinutes);
        }
        else
        {
            return new SmartSleepPlan(false, null, usesFallback, SmartSleepDecision.Disabled);
        }

        var minimum = TimeSpan.FromMinutes(Math.Max(1, settings.MinimumOpportunityMinutes));
        return wakeAt - nowUtc >= minimum
            ? new SmartSleepPlan(
                true,
                wakeAt,
                usesFallback,
                SmartSleepDecision.Sleep,
                coalescingMinutes)
            : new SmartSleepPlan(
                false,
                wakeAt,
                usesFallback,
                SmartSleepDecision.OpportunityTooShort,
                coalescingMinutes);
    }
}
