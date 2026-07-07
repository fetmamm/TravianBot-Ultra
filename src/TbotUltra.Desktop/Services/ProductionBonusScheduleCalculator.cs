using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public static class ProductionBonusScheduleCalculator
{
    private static readonly TimeSpan DailyResetTime = TimeSpan.FromHours(9);

    public static DateTimeOffset ResolveNextAttemptUtc(
        ProductionBonusDomParser.ProductionBonusResourceState state,
        DateTimeOffset nowUtc,
        TimeSpan serverUtcOffset,
        TimeSpan delay)
    {
        var now = nowUtc.ToUniversalTime();
        if (state.NextAttemptSeconds == 0)
        {
            return now;
        }

        if (state.NextAttemptSeconds == ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds)
        {
            var resetUtc = ResolveNextDailyResetUtc(now, serverUtcOffset);
            var bonusEndsUtc = now.AddSeconds(Math.Max(0, state.RemainingSeconds));
            var eligibleUtc = state.Bonus == 15 && bonusEndsUtc > resetUtc
                ? bonusEndsUtc
                : resetUtc;
            return eligibleUtc.Add(delay);
        }

        return now.AddSeconds(Math.Max(0, state.NextAttemptSeconds)).Add(delay);
    }

    public static DateTimeOffset ResolveNextDailyResetUtc(DateTimeOffset nowUtc, TimeSpan serverUtcOffset)
    {
        var serverNow = nowUtc.ToUniversalTime().ToOffset(serverUtcOffset);
        var resetServer = new DateTimeOffset(serverNow.Date.Add(DailyResetTime), serverUtcOffset);
        if (serverNow >= resetServer)
        {
            resetServer = resetServer.AddDays(1);
        }

        return resetServer.ToUniversalTime();
    }
}
