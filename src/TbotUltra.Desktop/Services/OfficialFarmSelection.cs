using System.Globalization;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class OfficialFarmSelection
{
    public static IReadOnlyList<FarmCoordinate> Filter(
        IEnumerable<TravcoListStore.TravcoSavedRow> sourceRows,
        IReadOnlySet<string> existingCoordinates,
        int amount,
        string order,
        string populationMode,
        long populationLimit,
        double? maximumDistance,
        bool skipDuplicates)
    {
        if (amount <= 0)
        {
            return [];
        }

        var rows = sourceRows
            .Where(row => row.Selected)
            .Where(row => TryParseCoordinates(row.Coordinates, out _, out _));

        rows = populationMode switch
        {
            "under" => rows.Where(row => row.Pop.HasValue && row.Pop.Value <= populationLimit),
            "over" => rows.Where(row => row.Pop.HasValue && row.Pop.Value >= populationLimit),
            _ => rows,
        };

        if (maximumDistance.HasValue)
        {
            rows = rows.Where(row => row.Distance.HasValue && row.Distance.Value <= maximumDistance.Value);
        }

        rows = order switch
        {
            "distance_desc" => rows.OrderByDescending(row => row.Distance ?? double.MinValue),
            "pop_desc" => rows.OrderByDescending(row => row.Pop ?? long.MinValue),
            "pop_asc" => rows.OrderBy(row => row.Pop ?? long.MaxValue),
            _ => rows.OrderBy(row => row.Distance ?? double.MaxValue),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FarmCoordinate>();
        foreach (var row in rows)
        {
            if (!TryParseCoordinates(row.Coordinates, out var x, out var y))
            {
                continue;
            }

            var key = $"{x}|{y}";
            if (!seen.Add(key) || (skipDuplicates && existingCoordinates.Contains(key)))
            {
                continue;
            }

            result.Add(new FarmCoordinate(x, y));
            if (result.Count >= amount)
            {
                break;
            }
        }

        return result;
    }

    private static bool TryParseCoordinates(string? value, out int x, out int y)
    {
        x = 0;
        y = 0;
        var match = Regex.Match(value ?? string.Empty, @"^\s*[\[(]?\s*(-?\d+)\s*\|\s*(-?\d+)\s*[\])]?\s*$");
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
               && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }
}
