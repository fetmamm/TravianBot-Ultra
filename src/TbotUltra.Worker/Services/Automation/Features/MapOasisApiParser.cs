using System.Globalization;
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
            if (!TryReadCoordinate(tile, "x", out var x)
                || !TryReadCoordinate(tile, "y", out var y)
                || !TryReadInt(tile, "did", out var did)
                || did != -1
                || !TryReadString(tile, "title", out var title)
                || (title != "{k.fo}" && title != "{k.bt}")
                || !TryReadString(tile, "text", out var text))
            {
                continue;
            }

            // Travian embeds Unicode bidi-control characters in the tile text, including between the
            // bonus tokens, which breaks the whitespace matching in every regex below. Strip once.
            var cleanText = StripFormatChars(text);
            if (!TryMapType(cleanText, out var oasisType, out var filterType))
            {
                continue;
            }

            var occupied = title == "{k.bt}"
                || (tile.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.Number);

            // Animals only exist on unoccupied oases; owner/alliance only on occupied ones.
            var animals = occupied ? string.Empty : ParseAnimals(cleanText);
            var (ownerPlayer, ownerAlliance) = occupied ? ParseOwner(cleanText) : (string.Empty, string.Empty);
            result.Add(new MapOasisEntry(x, y, occupied, oasisType, filterType, animals, ownerPlayer, ownerAlliance));
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

    // Travian embeds Unicode bidi-control characters (U+202D/U+202C) throughout the tile text,
    // including between the bonus tokens, which breaks \s+ in the regex.
    private static string StripFormatChars(string text)
    {
        if (!text.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            return text;
        }

        return new string(text.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
    }

    // Maps the nature animal unit ids used on the map (u31-u40) to readable names.
    private static string AnimalName(int unitId) => unitId switch
    {
        31 => "Rat",
        32 => "Spider",
        33 => "Snake",
        34 => "Bat",
        35 => "Wild Boar",
        36 => "Wolf",
        37 => "Bear",
        38 => "Crocodile",
        39 => "Tiger",
        40 => "Elephant",
        _ => $"u{unitId}",
    };

    // Reads the animal garrison of an unoccupied oasis into "Wild Boar 5, Wolf 4, Bear 4".
    private static string ParseAnimals(string cleanText)
    {
        var parts = new List<string>();
        foreach (Match match in AnimalRegex().Matches(cleanText))
        {
            if (!int.TryParse(match.Groups["unit"].Value, out var unitId)
                || !int.TryParse(match.Groups["count"].Value, out var count)
                || count <= 0)
            {
                continue;
            }

            parts.Add($"{AnimalName(unitId)} {count}");
        }

        return string.Join(", ", parts);
    }

    // Reads the owner player name and alliance tag of an occupied oasis from the tile text.
    private static (string Player, string Alliance) ParseOwner(string cleanText)
    {
        return (ExtractTokenValue(cleanText, PlayerRegex()), ExtractTokenValue(cleanText, AllianceRegex()));
    }

    private static string ExtractTokenValue(string cleanText, Regex regex)
    {
        var match = regex.Match(cleanText);
        if (!match.Success)
        {
            return string.Empty;
        }

        // Captured value may contain HTML markup/entities (e.g. a profile link); strip and decode it.
        var raw = HtmlTagRegex().Replace(match.Groups["value"].Value, string.Empty);
        return System.Net.WebUtility.HtmlDecode(raw).Trim();
    }

    private static bool TryMapType(string cleanText, out string oasisType, out string filterType)
    {
        var bonuses = BonusRegex().Matches(cleanText)
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

    // Travian's map API has shipped coordinates both as flat top-level "x"/"y" and nested under a
    // "position" object, sometimes as numeric strings. Accept every variant so the scan is robust.
    private static bool TryReadCoordinate(JsonElement tile, string name, out int value)
    {
        value = 0;
        if (TryReadIntOrNumericString(tile, name, out value))
        {
            return true;
        }

        return (tile.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object
                && TryReadIntOrNumericString(position, name, out value))
            || (tile.TryGetProperty("coordinates", out var coordinates) && coordinates.ValueKind == JsonValueKind.Object
                && TryReadIntOrNumericString(coordinates, name, out value));
    }

    private static bool TryReadIntOrNumericString(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false,
        };
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

    [GeneratedRegex(@"unit\s+u(?<unit>\d+)""\s*>\s*</i>\s*<span\s+class=""value\s*""\s*>\s*(?<count>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AnimalRegex();

    [GeneratedRegex(@"\{k\.spieler\}\s*(?<value>.*?)\s*(?:<br\s*/?>|\{k\.|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PlayerRegex();

    [GeneratedRegex(@"\{k\.allianz\}\s*(?<value>.*?)\s*(?:<br\s*/?>|\{k\.|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AllianceRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}

