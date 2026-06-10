using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

internal static partial class MapOasisApiParser
{
    public static IReadOnlyList<MapOasisEntry> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tiles", out var tiles)
            || tiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The Travian map API response did not contain a tiles array.");
        }

        var result = new List<MapOasisEntry>();
        foreach (var tile in tiles.EnumerateArray())
        {
            if (!TryReadInt(tile, "x", out var x)
                || !TryReadInt(tile, "y", out var y)
                || !TryReadInt(tile, "did", out var did)
                || did != -1
                || !TryReadString(tile, "title", out var title)
                || (title != "{k.fo}" && title != "{k.bt}")
                || !TryReadString(tile, "text", out var text)
                || !TryMapType(text, out var oasisType, out var filterType))
            {
                continue;
            }

            var occupied = title == "{k.bt}"
                || (tile.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.Number);
            result.Add(new MapOasisEntry(x, y, occupied, oasisType, filterType));
        }

        return result;
    }

    public static IReadOnlyList<(int X, int Y)> CreateScanCenters(
        int minimumCoordinate = -200,
        int maximumCoordinate = 200,
        int tileRadius = 15)
    {
        if (minimumCoordinate > maximumCoordinate || tileRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCoordinate));
        }

        var width = (tileRadius * 2) + 1;
        var axis = new List<int>();
        for (var start = minimumCoordinate; start <= maximumCoordinate; start += width)
        {
            axis.Add(start + tileRadius);
        }

        return axis.SelectMany(y => axis.Select(x => (x, y))).ToList();
    }

    private static bool TryMapType(string text, out string oasisType, out string filterType)
    {
        var bonuses = BonusRegex().Matches(text)
            .Select(match => (
                Resource: int.Parse(match.Groups["resource"].Value),
                Percent: int.Parse(match.Groups["percent"].Value)))
            .Distinct()
            .OrderBy(item => item.Resource)
            .ToList();

        (oasisType, filterType) = bonuses switch
        {
            [{ Resource: 1, Percent: 25 }] => ("Wood 25%", "Wood"),
            [{ Resource: 1, Percent: 50 }] => ("Wood 50%", "Wood"),
            [{ Resource: 2, Percent: 25 }] => ("Clay 25%", "Clay"),
            [{ Resource: 2, Percent: 50 }] => ("Clay 50%", "Clay"),
            [{ Resource: 3, Percent: 25 }] => ("Iron 25%", "Iron"),
            [{ Resource: 3, Percent: 50 }] => ("Iron 50%", "Iron"),
            [{ Resource: 4, Percent: 25 }] => ("Crop 25%", "Crop"),
            [{ Resource: 4, Percent: 50 }] => ("Crop 50%", "Crop"),
            [{ Resource: 1, Percent: 25 }, { Resource: 4, Percent: 25 }] => ("Wood+Crop", "Wood+Crop"),
            [{ Resource: 2, Percent: 25 }, { Resource: 4, Percent: 25 }] => ("Clay+Crop", "Clay+Crop"),
            [{ Resource: 3, Percent: 25 }, { Resource: 4, Percent: 25 }] => ("Iron+Crop", "Iron+Crop"),
            _ => (string.Empty, string.Empty),
        };
        return oasisType.Length > 0;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    [GeneratedRegex(@"\{a:r(?<resource>[1-4])\}(?:\s+\{a\.r[1-4]\})?\s+(?<percent>25|50)%", RegexOptions.IgnoreCase)]
    private static partial Regex BonusRegex();
}

