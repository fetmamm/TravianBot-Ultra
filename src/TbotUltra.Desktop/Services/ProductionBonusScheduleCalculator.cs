using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public static class ProductionBonusScheduleCalculator
{
    public const int DefaultDailyResetHour = 9;

    // While the daily reset hour is not known yet (auto-learn in progress), the +15% "waiting for reset"
    // case reschedules by this interval so the bot polls the Advantages tab hourly and discovers the reset.
    private static readonly TimeSpan DefaultUnknownResetPollInterval = TimeSpan.FromHours(1);

    public static DateTimeOffset ResolveNextAttemptUtc(
        ProductionBonusDomParser.ProductionBonusResourceState state,
        DateTimeOffset nowUtc,
        TimeSpan serverUtcOffset,
        TimeSpan delay,
        int? dailyResetHour = DefaultDailyResetHour,
        TimeSpan? unknownResetPollInterval = null)
    {
        var now = nowUtc.ToUniversalTime();
        if (state.NextAttemptSeconds == 0)
        {
            return now;
        }

        if (state.NextAttemptSeconds == ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds)
        {
            // Reset hour unknown (auto mode, still learning): poll hourly to catch when the video re-enables.
            if (dailyResetHour is null)
            {
                return now.Add(unknownResetPollInterval ?? DefaultUnknownResetPollInterval);
            }

            var resetUtc = ResolveNextDailyResetUtc(now, serverUtcOffset, dailyResetHour.Value);
            var bonusEndsUtc = now.AddSeconds(Math.Max(0, state.RemainingSeconds));
            var eligibleUtc = state.Bonus == 15 && bonusEndsUtc > resetUtc
                ? bonusEndsUtc
                : resetUtc;
            return eligibleUtc.Add(delay);
        }

        return now.AddSeconds(Math.Max(0, state.NextAttemptSeconds)).Add(delay);
    }

    public static DateTimeOffset ResolveNextDailyResetUtc(
        DateTimeOffset nowUtc,
        TimeSpan serverUtcOffset,
        int resetHour = DefaultDailyResetHour)
    {
        var hour = Math.Clamp(resetHour, 0, 23);
        var serverNow = nowUtc.ToUniversalTime().ToOffset(serverUtcOffset);
        var resetServer = new DateTimeOffset(serverNow.Date.AddHours(hour), serverUtcOffset);
        if (serverNow >= resetServer)
        {
            resetServer = resetServer.AddDays(1);
        }

        return resetServer.ToUniversalTime();
    }
}
