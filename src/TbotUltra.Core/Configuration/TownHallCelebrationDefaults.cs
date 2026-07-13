namespace TbotUltra.Core.Configuration;

public static class TownHallCelebrationDefaults
{
    public const string Small = "small";
    public const string Big = "big";

    // How many celebrations to keep active. 2 = one ongoing + one queued (requires Travian Plus; falls
    // back to one when the server won't let a second be queued). Default two.
    public const int DefaultCount = 2;
    public const int MinCount = 1;
    public const int MaxCount = 2;

    // Random delay (minutes) after a celebration slot frees before starting the next one, so the bot does
    // not restart the instant the timer hits zero. 0/0 disables it.
    public const double DefaultRestartDelayMinMinutes = 15;
    public const double DefaultRestartDelayMaxMinutes = 75;

    public static string NormalizeMode(string? value)
    {
        var normalized = value?.Trim();
        return string.Equals(normalized, Big, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "great", StringComparison.OrdinalIgnoreCase)
            ? Big
            : Small;
    }

    public static int NormalizeCount(int value) => Math.Clamp(value, MinCount, MaxCount);
}
