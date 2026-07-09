using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProductionBonusScheduleCalculatorTests
{
    private static readonly TimeSpan ServerOffset = TimeSpan.FromHours(2);
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(10);

    [Fact]
    public void ResolveNextAttemptUtc_BeforeReset_UsesToday0900PlusDelay()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 7, 6, 32, 0, TimeSpan.Zero); // 08:32 server time
        var state = DailyResetState(bonus: 0, remainingSeconds: 0);

        var next = ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(state, nowUtc, ServerOffset, Delay);

        Assert.Equal(new DateTimeOffset(2026, 7, 7, 7, 10, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void ResolveNextAttemptUtc_AfterReset_UsesTomorrow0900PlusDelay()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 7, 7, 1, 0, TimeSpan.Zero); // 09:01 server time
        var state = DailyResetState(bonus: 0, remainingSeconds: 0);

        var next = ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(state, nowUtc, ServerOffset, Delay);

        Assert.Equal(new DateTimeOffset(2026, 7, 8, 7, 10, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void ResolveNextAttemptUtc_Active15EndingAfterReset_UsesBonusEndPlusDelay()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 7, 6, 32, 0, TimeSpan.Zero); // 08:32 server time
        var state = DailyResetState(bonus: 15, remainingSeconds: 4 * 60 * 60);

        var next = ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(state, nowUtc, ServerOffset, Delay);

        Assert.Equal(new DateTimeOffset(2026, 7, 7, 10, 42, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void ResolveNextAttemptUtc_Active15EndingBeforeReset_UsesResetPlusDelay()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 7, 6, 32, 0, TimeSpan.Zero); // 08:32 server time
        var state = DailyResetState(bonus: 15, remainingSeconds: 10 * 60);

        var next = ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(state, nowUtc, ServerOffset, Delay);

        Assert.Equal(new DateTimeOffset(2026, 7, 7, 7, 10, 0, TimeSpan.Zero), next);
    }

    private static ProductionBonusDomParser.ProductionBonusResourceState DailyResetState(int bonus, int remainingSeconds)
        => new(
            "lumber",
            bonus,
            remainingSeconds,
            ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds,
            false);
}
