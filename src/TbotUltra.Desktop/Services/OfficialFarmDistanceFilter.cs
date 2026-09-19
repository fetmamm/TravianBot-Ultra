using System.Globalization;

namespace TbotUltra.Desktop.Services;

public static class OfficialFarmDistanceFilter
{
    public const string All = "all";
    public const string Within = "within";
    public const string AtLeast = "at_least";
    public const string Between = "between";

    public static bool TryResolve(
        string? mode,
        string? firstValue,
        string? secondValue,
        out double? minimumDistance,
        out double? maximumDistance)
    {
        minimumDistance = null;
        maximumDistance = null;
        var normalizedMode = mode?.Trim().ToLowerInvariant() ?? All;
        if (normalizedMode == All)
        {
            return true;
        }

        if (!TryReadDistance(firstValue, out var first))
        {
            return false;
        }

        if (normalizedMode == Within)
        {
            maximumDistance = first;
            return true;
        }

        if (normalizedMode == AtLeast)
        {
            minimumDistance = first;
            return true;
        }

        if (normalizedMode != Between || !TryReadDistance(secondValue, out var second) || first > second)
        {
            return false;
        }

        minimumDistance = first;
        maximumDistance = second;
        return true;
    }

    private static bool TryReadDistance(string? value, out double distance)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
            && double.IsFinite(distance)
            && distance >= 0;
}
