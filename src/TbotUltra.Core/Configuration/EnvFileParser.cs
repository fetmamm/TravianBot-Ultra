namespace TbotUltra.Core.Configuration;

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

            var splitIndex = line.IndexOf('=');
            var key = line[..splitIndex].Trim();
            var value = line[(splitIndex + 1)..].Trim().Trim('"').Trim('\'');
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = value;
        }

        return values;
    }
}
