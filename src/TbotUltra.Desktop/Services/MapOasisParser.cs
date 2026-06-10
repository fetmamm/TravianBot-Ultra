using System.Globalization;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public static class MapOasisParser
{
    public static List<OasisInfo> Parse(
        IEnumerable<string> lines,
        bool includeOccupied,
        IReadOnlyCollection<string> selectedTypes)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(selectedTypes);

        var selected = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
        var result = new List<OasisInfo>();
        foreach (var line in lines)
        {
            if (!TryParseValues(line, out var values)
                || values.Count < 7
                || !TryParseInt(values[0], out var x)
                || !TryParseInt(values[1], out var y)
                || !TryParseInt(values[2], out var landscape)
                || !TryParseInt(values[3], out var type)
                || type != 3
                || !TryParseInt(values[6], out var playerId)
                || !TryMapLandscape(landscape, out var oasisType, out var filterType)
                || !selected.Contains(filterType))
            {
                continue;
            }

            var isOccupied = playerId != 0;
            if (!includeOccupied && isOccupied)
            {
                continue;
            }

            result.Add(new OasisInfo
            {
                X = x,
                Y = y,
                Landscape = landscape,
                IsOccupied = isOccupied,
                OasisType = oasisType,
            });
        }

        return result;
    }

    public static bool TryMapLandscape(int landscape, out string oasisType, out string filterType)
    {
        (oasisType, filterType) = landscape switch
        {
            10 or 11 => ("Wood 25%", "Wood"),
            40 or 41 => ("Wood 50%", "Wood"),
            12 or 13 => ("Clay 25%", "Clay"),
            42 or 43 => ("Clay 50%", "Clay"),
            14 or 15 => ("Iron 25%", "Iron"),
            44 or 45 => ("Iron 50%", "Iron"),
            16 or 17 => ("Crop 25%", "Crop"),
            46 or 47 => ("Crop 50%", "Crop"),
            20 or 21 => ("Wood+Crop", "Wood+Crop"),
            22 or 23 => ("Clay+Crop", "Clay+Crop"),
            24 or 25 => ("Iron+Crop", "Iron+Crop"),
            _ => (string.Empty, string.Empty),
        };
        return oasisType.Length > 0;
    }

    private static bool TryParseValues(string? line, out List<string> values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var valuesIndex = line.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase);
        if (valuesIndex < 0)
        {
            return false;
        }

        var start = line.IndexOf('(', valuesIndex);
        var end = line.LastIndexOf(')');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = start + 1; index < end; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character == '\\' && index + 1 < end)
                {
                    current.Append(line[++index]);
                    continue;
                }

                if (character == '\'')
                {
                    if (index + 1 < end && line[index + 1] == '\'')
                    {
                        current.Append('\'');
                        index++;
                        continue;
                    }

                    quoted = false;
                    continue;
                }

                current.Append(character);
                continue;
            }

            if (character == '\'')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            values.Clear();
            return false;
        }

        values.Add(current.ToString().Trim());
        return true;
    }

    private static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}
