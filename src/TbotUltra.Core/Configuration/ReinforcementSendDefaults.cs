namespace TbotUltra.Core.Configuration;

public static class ReinforcementSendDefaults
{
    // Automatic reinforcement sends are scheduled a random pick in [min, max] minutes apart.
    public const int DefaultSendMinMinutes = 60;
    public const int DefaultSendMaxMinutes = 120;

    public static int NormalizeSendMinMinutes(int value)
    {
        return value > 0 ? value : DefaultSendMinMinutes;
    }

    public static int NormalizeSendMaxMinutes(int value)
    {
        return value > 0 ? value : DefaultSendMaxMinutes;
    }

    // Random send delay within [min, max] minutes. Max below min collapses to min.
    public static TimeSpan CalculateSendDelay(int minMinutes, int maxMinutes)
    {
        var minSeconds = NormalizeSendMinMinutes(minMinutes) * 60;
        var maxSeconds = Math.Max(minSeconds, NormalizeSendMaxMinutes(maxMinutes) * 60);
        return TimeSpan.FromSeconds(Random.Shared.Next(minSeconds, maxSeconds + 1));
    }
}
