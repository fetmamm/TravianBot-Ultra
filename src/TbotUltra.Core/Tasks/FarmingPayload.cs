using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

// Note: the dispatch delay is intentionally NOT carried in this payload. It is a live setting read
// from BotOptions at execution time, so changing it while the continuous loop runs takes effect on
// the next send cycle instead of being frozen at enqueue time.
//
// FarmListIds carries the stable Travian list ids (lid) for the selected lists so matching survives
// a village/list rename. FarmListNames is still carried for display and as a fallback when ids are
// unavailable (e.g. selections saved before lids existed).
public sealed record FarmingPayload(IReadOnlyList<string> FarmListNames, IReadOnlyList<string>? FarmListIds = null)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out FarmingPayload? result)
    {
        var names = ParseList(ReadTrimmed(payload, BotOptionPayloadKeys.ContinuousFarmListNames));
        var ids = ParseList(ReadTrimmed(payload, BotOptionPayloadKeys.ContinuousFarmListIds));
        result = new FarmingPayload(names, ids);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ContinuousFarmListNames] = JoinDistinct(FarmListNames),
        };

        var ids = JoinDistinct(FarmListIds ?? []);
        if (!string.IsNullOrWhiteSpace(ids))
        {
            dictionary[BotOptionPayloadKeys.ContinuousFarmListIds] = ids;
        }

        return dictionary;
    }

    private static string JoinDistinct(IReadOnlyList<string> values)
    {
        return string.Join(",", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? ReadTrimmed(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static IReadOnlyList<string> ParseList(string? raw)
    {
        return (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
