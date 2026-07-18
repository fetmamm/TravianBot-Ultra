namespace TbotUltra.Core.Configuration;

using System.Text.Json;

public static class EnvFileParser
{
    public static Dictionary<string, string> ReadValues(string envPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(envPath))
        {
            return values;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var splitIndex = rawLine.IndexOf('=');
            var key = rawLine[..splitIndex].Trim();
            var value = ParseValue(rawLine[(splitIndex + 1)..]);
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = value;
        }

        return values;
    }

    public static string FormatValue(string? value)
        => JsonSerializer.Serialize(value ?? string.Empty);

    private static string ParseValue(string rawValue)
    {
        var trimmed = rawValue.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? string.Empty;
            }
            catch (JsonException)
            {
                // Preserve compatibility with hand-edited legacy files that used unescaped quotes.
                return trimmed[1..^1];
            }
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
