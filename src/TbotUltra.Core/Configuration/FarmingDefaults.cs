namespace TbotUltra.Core.Configuration;

public static class FarmingDefaults
{
    public const string SendModeListPerList = "list_per_list";
    public const string SendModeSharedSchedule = "shared_schedule";
    public const string SendModeAllAtOnce = "all_at_once";

    // Delay between farm sends is a random pick in [min, max] minutes.
    public const int DefaultDispatchDelayMinMinutes = 30;
    public const int DefaultDispatchDelayMaxMinutes = 90;
    public const bool ShowLastSentTimer = true;
    public const bool OnlyCreateReportsWithLosses = true;
    public const bool LastSentLimitEnabled = true;
    public const int DefaultLastSentLimitHours = 24;
    public const int MaxLastSentLimitHours = 120;

    public static int NormalizeLastSentLimitHours(int value)
        => value <= 0 ? DefaultLastSentLimitHours : Math.Min(value, MaxLastSentLimitHours);

    public static int NormalizeDispatchDelayMinMinutes(int value)
    {
        return value > 0 ? value : DefaultDispatchDelayMinMinutes;
    }

    public static int NormalizeDispatchDelayMaxMinutes(int value)
    {
        return value > 0 ? value : DefaultDispatchDelayMaxMinutes;
    }

    // Random dispatch delay in whole seconds within [min, max] minutes. Max below min collapses to min.
    public static int CalculateDispatchDelaySeconds(int minMinutes, int maxMinutes)
    {
        var minSeconds = NormalizeDispatchDelayMinMinutes(minMinutes) * 60;
        var maxSeconds = Math.Max(minSeconds, NormalizeDispatchDelayMaxMinutes(maxMinutes) * 60);
        return Random.Shared.Next(minSeconds, maxSeconds + 1);
    }

    public static string NormalizeSendMode(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, SendModeAllAtOnce, StringComparison.OrdinalIgnoreCase))
        {
            return SendModeAllAtOnce;
        }

        return string.Equals(normalized, SendModeSharedSchedule, StringComparison.OrdinalIgnoreCase)
            ? SendModeSharedSchedule
            : SendModeListPerList;
    }
}
