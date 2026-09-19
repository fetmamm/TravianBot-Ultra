using System.Text.Json;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static partial class HeroAdventurePageParser
{
    [GeneratedRegex("class\\s*=\\s*[\\\"'][^\\\"']*\\bnoRallyPointInHomeVillage\\b[^\\\"']*[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex MissingRallyPointClassRegex();

    internal static HeroRallyPointRepairRequest? ParseMissingRallyPoint(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || !MissingRallyPointClassRegex().IsMatch(html))
        {
            return null;
        }

        var viewData = ExtractJsonObjectAfterToken(html, "viewData:");
        if (viewData is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(viewData);
            var ownPlayer = document.RootElement.GetProperty("data").GetProperty("ownPlayer");
            var homeVillage = ownPlayer.GetProperty("hero").GetProperty("homeVillage");
            var villageId = ReadInt(homeVillage, "id");
            var villageName = homeVillage.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()?.Trim()
                : null;
            if (villageId is not > 0 || string.IsNullOrWhiteSpace(villageName))
            {
                return null;
            }

            int? coordX = null;
            int? coordY = null;
            foreach (var village in ownPlayer.GetProperty("villages").EnumerateArray())
            {
                if (ReadInt(village, "id") != villageId)
                {
                    continue;
                }

                if (village.TryGetProperty("hasRallyPoint", out var rallyPointElement)
                    && rallyPointElement.ValueKind == JsonValueKind.True)
                {
                    return null;
                }

                coordX = ReadInt(village, "x");
                coordY = ReadInt(village, "y");
                break;
            }

            return new HeroRallyPointRepairRequest(villageId.Value, villageName, coordX, coordY);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null,
        };
    }

    private static string? ExtractJsonObjectAfterToken(string source, string token)
    {
        var tokenIndex = source.IndexOf(token, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return null;
        }

        var start = source.IndexOf('{', tokenIndex + token.Length);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        return null;
    }
}
