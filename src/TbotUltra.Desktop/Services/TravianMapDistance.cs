using System.Globalization;
using System.Text.RegularExpressions;

namespace TbotUltra.Desktop.Services;

public static class TravianMapDistance
{
    private const int DefaultWorldSize = 401;

    public static double Calculate(int fromX, int fromY, int toX, int toY, int worldSize = DefaultWorldSize)
    {
        if (worldSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(worldSize), "World size must be greater than zero.");
        }

        var dx = CalculateWrappedDelta(fromX, toX, worldSize);
        var dy = CalculateWrappedDelta(fromY, toY, worldSize);
        return Math.Sqrt(Math.Pow(dx, 2) + Math.Pow(dy, 2));
    }

    public static double CalculateRounded(int fromX, int fromY, int toX, int toY, int worldSize = DefaultWorldSize)
    {
        return Math.Round(Calculate(fromX, fromY, toX, toY, worldSize), 2);
    }

    public static bool TryParseCoordinates(string? value, out int x, out int y)
    {
        x = 0;
        y = 0;
        var match = Regex.Match(value ?? string.Empty, @"^\s*[\[(]?\s*(-?\d+)\s*\|\s*(-?\d+)\s*[\])]?\s*$");
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
               && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    private static int CalculateWrappedDelta(int from, int to, int worldSize)
    {
        var raw = Math.Abs(to - from) % worldSize;
        return Math.Min(raw, worldSize - raw);
    }
}
