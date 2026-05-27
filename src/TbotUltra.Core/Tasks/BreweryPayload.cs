using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record BreweryPayload(bool AutoCelebrationEnabled)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out BreweryPayload? result)
    {
        result = null;
        if (!payload.TryGetValue(BotOptionPayloadKeys.BreweryAutoCelebrationEnabled, out var raw))
        {
            result = new BreweryPayload(true);
            return true;
        }

        if (!bool.TryParse(raw, out var enabled))
        {
            return false;
        }

        result = new BreweryPayload(enabled);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BreweryAutoCelebrationEnabled] = AutoCelebrationEnabled ? "true" : "false",
        };
    }
}
