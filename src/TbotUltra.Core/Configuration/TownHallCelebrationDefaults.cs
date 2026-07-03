namespace TbotUltra.Core.Configuration;

public static class TownHallCelebrationDefaults
{
    public const string Small = "small";
    public const string Big = "big";

    public static string NormalizeMode(string? value)
    {
        var normalized = value?.Trim();
        return string.Equals(normalized, Big, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "great", StringComparison.OrdinalIgnoreCase)
            ? Big
            : Small;
    }
}
