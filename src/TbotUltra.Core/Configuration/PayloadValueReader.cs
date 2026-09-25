namespace TbotUltra.Core.Configuration;

internal static class PayloadValueReader
{
    internal static bool TryReadInt(string key, string value, string expectedKey, out int parsed)
    {
        parsed = 0;
        return key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsed);
    }

    internal static bool TryReadLong(string key, string value, string expectedKey, out long parsed)
    {
        parsed = 0;
        return key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out parsed);
    }

    internal static bool TryReadDouble(string key, string value, string expectedKey, out double parsed)
    {
        parsed = 0;
        return key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out parsed);
    }

    internal static bool TryReadBool(string key, string value, string expectedKey, out bool parsed)
    {
        parsed = false;
        return key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsed);
    }
}
