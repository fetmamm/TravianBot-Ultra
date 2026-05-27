using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record FarmingPayload(IReadOnlyList<string> FarmListNames, int DispatchDelayMinutes)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out FarmingPayload? result)
    {
        result = null;
        var names = ParseNames(ReadTrimmed(payload, BotOptionPayloadKeys.ContinuousFarmListNames));
        var delay = 1;
        if (payload.TryGetValue(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes, out var delayRaw))
        {
            if (!int.TryParse(delayRaw, out var parsedDelay))
            {
                return false;
            }

            delay = Math.Clamp(parsedDelay, 1, 5);
        }

        result = new FarmingPayload(names, delay);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ContinuousFarmListNames] = string.Join(",", FarmListNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
            [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes] = Math.Clamp(DispatchDelayMinutes, 1, 5).ToString(),
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
