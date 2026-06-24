using System.Globalization;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

public static partial class TravcoInactiveSearchParser
{
    public static TravcoScrapeResult Parse(TravcoRawPage rawPage)
    {
        ArgumentNullException.ThrowIfNull(rawPage);

        var rows = rawPage.Rows
            .Select(ParseRow)
            .Where(row => row is not null)
            .Cast<TravcoRow>()
            .ToList();

        return new TravcoScrapeResult(
            Math.Max(1, rawPage.PageNumber),
            Math.Max(1, rawPage.TotalPages),
            rows);
    }

    private static TravcoRow? ParseRow(TravcoRawRow rawRow)
    {
        if (rawRow.Cells.Count < 4)
        {
            return null;
        }

        // Travco rows are: checkbox, Distance, Travian account, Village, newest population, older populations...
        var offset = rawRow.Cells.Count > 0 && string.IsNullOrWhiteSpace(rawRow.Cells[0]) ? 1 : 0;
        if (rawRow.Cells.Count < offset + 4)
        {
            return null;
        }

        var distance = ParseDouble(rawRow.Cells[offset]);
        var account = rawRow.Cells[offset + 1].Trim();
        var village = rawRow.Cells[offset + 2].Trim();
        var pop = ParseLong(rawRow.Cells[offset + 3]);
        var coordinates = ParseCoordinates(rawRow.VillageHref) ?? ParseCoordinates(rawRow.Cells[offset + 2]) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(account) && string.IsNullOrWhiteSpace(village))
        {
            return null;
        }

        return new TravcoRow(distance, account, village, pop, coordinates);
    }

    private static double? ParseDouble(string? value)
    {
        var normalized = NormalizeNumber(value);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseLong(string? value)
    {
        var normalized = NormalizeNumber(value);
        return long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeNumber(string? value)
    {
        return string.Concat((value ?? string.Empty)
            .Trim()
            .Replace(',', '.')
            .Where(ch => char.IsDigit(ch) || ch is '-' or '.'));
    }

    private static string? ParseCoordinates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var queryMatch = QueryCoordinatesRegex().Match(value);
        if (queryMatch.Success)
        {
            return $"{queryMatch.Groups["x"].Value}|{queryMatch.Groups["y"].Value}";
        }

        var textMatch = TextCoordinatesRegex().Match(value);
        return textMatch.Success
            ? $"{textMatch.Groups["x"].Value}|{textMatch.Groups["y"].Value}"
            : null;
    }

    [GeneratedRegex(@"[?&]x=(?<x>-?\d+).*?[?&]y=(?<y>-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex QueryCoordinatesRegex();

    [GeneratedRegex(@"\(?\s*(?<x>-?\d+)\s*[|,]\s*(?<y>-?\d+)\s*\)?")]
    private static partial Regex TextCoordinatesRegex();
}
