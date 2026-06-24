using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

public enum FarmListLossState
{
    Unknown,
    NoLoss,
    Loss,
}

public static class FarmListLossStateClassifier
{
    public static FarmListLossState Classify(string? classNames)
    {
        if (string.IsNullOrWhiteSpace(classNames))
        {
            return FarmListLossState.Unknown;
        }

        var normalized = classNames.Trim().ToLowerInvariant();
        if (normalized.Contains("attack_lost", StringComparison.Ordinal))
        {
            return FarmListLossState.Loss;
        }

        if (normalized.Contains("attack_won_withlosses", StringComparison.Ordinal))
        {
            return FarmListLossState.Loss;
        }

        if (normalized.Contains("attack_won_withoutlosses", StringComparison.Ordinal))
        {
            return FarmListLossState.NoLoss;
        }

        return FarmListLossState.Unknown;
    }

    public static bool IsUnoccupiedOasis(string? targetName)
    {
        return string.Equals(CleanTargetName(targetName), "Unoccupied oasis", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanTargetName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value
            .Replace("\u202A", string.Empty)
            .Replace("\u202B", string.Empty)
            .Replace("\u202C", string.Empty)
            .Replace("\u202D", string.Empty)
            .Replace("\u202E", string.Empty)
            .Replace("\u200E", string.Empty)
            .Replace("\u200F", string.Empty)
            .Replace('−', '-');
        cleaned = Regex.Replace(cleaned, @"\s*\(\s*-?\d+\s*\|\s*-?\d+\s*\)\s*$", string.Empty);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }
}
