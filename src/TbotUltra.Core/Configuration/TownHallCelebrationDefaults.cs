namespace TbotUltra.Core.Configuration;

public static class TownHallCelebrationDefaults
{
    public const string Small = "small";
    public const string Big = "big";

    public static string NormalizeMode(string? value)
    {
        return string.Equals(value?.Trim(), Big, StringComparison.OrdinalIgnoreCase)
            ? Big
            : Small;
    }
}
