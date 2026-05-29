using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

// Note: the dispatch delay is intentionally NOT carried in this payload. It is a live setting read
// from BotOptions at execution time, so changing it while the continuous loop runs takes effect on
// the next send cycle instead of being frozen at enqueue time.
public sealed record FarmingPayload(IReadOnlyList<string> FarmListNames)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out FarmingPayload? result)
    {
        var names = ParseNames(ReadTrimmed(payload, BotOptionPayloadKeys.ContinuousFarmListNames));
        result = new FarmingPayload(names);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ContinuousFarmListNames] = string.Join(",", FarmListNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
        };
    }

    private static string? ReadTrimmed(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static IReadOnlyList<string> ParseNames(string? raw)
    {
        return (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
