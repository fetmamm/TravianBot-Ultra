namespace TbotUltra.Core.Configuration;

internal sealed record ReinforcementPayloadValues(
    bool Enabled,
    string TargetVillageName,
    List<string> SourceVillageNames,
    List<ReinforcementTroopRule> TroopRules,
    int SendMinMinutes,
    int SendMaxMinutes);

internal static class ReinforcementPayloadApplier
{
    internal static ReinforcementPayloadValues Apply(
        BotOptions source,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ReinforcementPayloadValues(
            source.ReinforcementsEnabled,
            source.ReinforcementsTargetVillageName,
            source.ReinforcementsSourceVillageNames,
            source.ReinforcementsTroopRules,
            source.ReinforcementsSendMinMinutes,
            source.ReinforcementsSendMaxMinutes);

        if (payload is null)
        {
            return result;
        }

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            if (key.Equals(BotOptionPayloadKeys.ReinforcementsEnabled, StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(value, out var enabled))
            {
                result = result with { Enabled = enabled };
            }
            else if (key.Equals(BotOptionPayloadKeys.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase))
            {
                result = result with { TargetVillageName = value };
            }
            else if (key.Equals(BotOptionPayloadKeys.ReinforcementsSourceVillageNames, StringComparison.OrdinalIgnoreCase))
            {
                result = result with { SourceVillageNames = ParseVillageNames(value) };
            }
            else if (key.Equals(BotOptionPayloadKeys.ReinforcementsTroopRules, StringComparison.OrdinalIgnoreCase))
            {
                result = result with { TroopRules = ParseTroopRules(value) };
            }
            else if (key.Equals(BotOptionPayloadKeys.ReinforcementsSendMinMinutes, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var sendMin))
            {
                result = result with { SendMinMinutes = ReinforcementSendDefaults.NormalizeSendMinMinutes(sendMin) };
            }
            else if (key.Equals(BotOptionPayloadKeys.ReinforcementsSendMaxMinutes, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var sendMax))
            {
                result = result with { SendMaxMinutes = ReinforcementSendDefaults.NormalizeSendMaxMinutes(sendMax) };
            }
        }

        return result;
    }

    private static List<string> ParseVillageNames(string value)
        => value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<ReinforcementTroopRule> ParseTroopRules(string value)
    {
        try
        {
            var rules = System.Text.Json.JsonSerializer.Deserialize<List<ReinforcementTroopRule>>(
                value,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return rules
                .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.TroopType))
                .Select(rule => rule.Normalize())
                .GroupBy(rule => $"{rule.AccountName}\u001f{rule.SourceVillageName}\u001f{rule.TroopType}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
