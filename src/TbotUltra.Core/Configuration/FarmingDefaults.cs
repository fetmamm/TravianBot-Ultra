namespace TbotUltra.Core.Configuration;

public static class FarmingDefaults
{
    public const string SendModeListPerList = "list_per_list";
    public const string SendModeAllAtOnce = "all_at_once";
    public const int DefaultDispatchDelayMinutes = 15;
    public const int DefaultDispatchDelayVariationPercent = 0;

    public static readonly int[] DispatchDelayMinuteChoices = [1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 90];
    public static readonly int[] DispatchDelayVariationPercentChoices = [0, 5, 10, 20, 50];

    public static string NormalizeSendMode(string? value)
    {
        return string.Equals(value?.Trim(), SendModeAllAtOnce, StringComparison.OrdinalIgnoreCase)
            ? SendModeAllAtOnce
            : SendModeListPerList;
    }

    public static int NormalizeDispatchDelayMinutes(int value)
    {
        if (DispatchDelayMinuteChoices.Contains(value))
        {
            return value;
        }

        if (value <= 0)
        {
            return DefaultDispatchDelayMinutes;
        }

        return DispatchDelayMinuteChoices
            .OrderBy(option => Math.Abs((long)option - value))
            .ThenBy(option => option)
            .First();
    }

    public static int NormalizeDispatchDelayVariationPercent(int value)
    {
        if (DispatchDelayVariationPercentChoices.Contains(value))
        {
            return value;
        }

        if (value <= 0)
        {
            return DefaultDispatchDelayVariationPercent;
        }

        return DispatchDelayVariationPercentChoices
            .OrderBy(option => Math.Abs(option - value))
            .ThenBy(option => option)
            .First();
    }
}
