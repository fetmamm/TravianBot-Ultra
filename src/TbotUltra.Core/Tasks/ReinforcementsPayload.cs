using System.Text.Json;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record ReinforcementsPayload(
    bool Enabled,
    string TargetVillageName,
    IReadOnlyList<string> SourceVillageNames,
    IReadOnlyList<ReinforcementTroopRule> TroopRules)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out ReinforcementsPayload? result)
    {
        result = null;
        if (!TryReadBool(payload, BotOptionPayloadKeys.ReinforcementsEnabled, defaultValue: true, out var enabled))
        {
            return false;
        }

        var target = ReadTrimmed(payload, BotOptionPayloadKeys.ReinforcementsTargetVillageName) ?? string.Empty;
        var sources = ParseNames(ReadTrimmed(payload, BotOptionPayloadKeys.ReinforcementsSourceVillageNames));
        var rules = new List<ReinforcementTroopRule>();
        if (payload.TryGetValue(BotOptionPayloadKeys.ReinforcementsTroopRules, out var rulesRaw)
            && !string.IsNullOrWhiteSpace(rulesRaw))
        {
            try
            {
                rules = JsonSerializer.Deserialize<List<ReinforcementTroopRule>>(rulesRaw) ?? [];
            }
            catch (JsonException)
            {
                return false;
            }
        }

        result = new ReinforcementsPayload(enabled, target, sources, rules);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ReinforcementsEnabled] = Enabled ? "true" : "false",
            [BotOptionPayloadKeys.ReinforcementsTargetVillageName] = TargetVillageName.Trim(),
            [BotOptionPayloadKeys.ReinforcementsSourceVillageNames] = string.Join(",", SourceVillageNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
            [BotOptionPayloadKeys.ReinforcementsTroopRules] = JsonSerializer.Serialize(TroopRules),
        };
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, string> payload, string key, bool defaultValue, out bool value)
    {
        value = defaultValue;
        return !payload.TryGetValue(key, out var raw) || bool.TryParse(raw, out value);
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
