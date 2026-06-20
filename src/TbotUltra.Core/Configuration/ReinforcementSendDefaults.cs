namespace TbotUltra.Core.Configuration;

public static class ReinforcementSendDefaults
{
    public const int DefaultIntervalHours = 5;
    public const int DefaultVariationPercent = 25;

    public static readonly int[] IntervalHourChoices = [1, 2, 5, 8, 12, 24];
    public static readonly int[] VariationPercentChoices = [0, 10, 25, 50, 90];

    public static int NormalizeIntervalHours(int value)
    {
        if (IntervalHourChoices.Contains(value))
        {
            return value;
        }

        if (value <= 0)
        {
            return DefaultIntervalHours;
        }

        return IntervalHourChoices
            .OrderBy(option => Math.Abs((long)option - value))
            .ThenBy(option => option)
            .First();
    }

    public static int NormalizeVariationPercent(int value)
    {
        if (VariationPercentChoices.Contains(value))
        {
            return value;
        }

        if (value <= 0)
        {
            return DefaultVariationPercent;
        }

        return VariationPercentChoices
            .OrderBy(option => Math.Abs(option - value))
            .ThenBy(option => option)
            .First();
    }

    public static TimeSpan CalculateSendDelay(int intervalHours, int variationPercent)
    {
        var normalizedHours = NormalizeIntervalHours(intervalHours);
        var normalizedVariation = NormalizeVariationPercent(variationPercent);
        var baseSeconds = TimeSpan.FromHours(normalizedHours).TotalSeconds;
        if (normalizedVariation <= 0)
        {
            return TimeSpan.FromSeconds(baseSeconds);
        }

        var variationSeconds = baseSeconds * normalizedVariation / 100d;
        var randomOffset = ((Random.Shared.NextDouble() * 2d) - 1d) * variationSeconds;
        return TimeSpan.FromSeconds(Math.Max(1d, baseSeconds + randomOffset));
    }
}
