using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

internal sealed record ReinforcementOptions(
    bool Enabled,
    string TargetVillageName,
    List<string> SourceVillageNames,
    List<ReinforcementTroopRule> TroopRules,
    int SendMinMinutes,
    int SendMaxMinutes);

internal static class ReinforcementOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.ReinforcementsEnabled,
        BotOptionPayloadKeys.ReinforcementsTargetVillageName,
        BotOptionPayloadKeys.ReinforcementsSourceVillageNames,
        BotOptionPayloadKeys.ReinforcementsTroopRules,
        BotOptionPayloadKeys.ReinforcementsSendMinMinutes,
        BotOptionPayloadKeys.ReinforcementsSendMaxMinutes,
    ];

    internal static ReinforcementOptions FromConfiguration(IConfiguration configuration)
        => new(
            configuration.GetValue(BotOptionPayloadKeys.ReinforcementsEnabled, false),
            configuration[BotOptionPayloadKeys.ReinforcementsTargetVillageName] ?? string.Empty,
            configuration.GetSection(BotOptionPayloadKeys.ReinforcementsSourceVillageNames).Get<List<string>>() ?? [],
            NormalizeTroopRules(configuration.GetSection(BotOptionPayloadKeys.ReinforcementsTroopRules).Get<List<ReinforcementTroopRule>>() ?? []),
            ReinforcementSendDefaults.NormalizeSendMinMinutes(configuration.GetValue(
                BotOptionPayloadKeys.ReinforcementsSendMinMinutes,
                ReinforcementSendDefaults.DefaultSendMinMinutes)),
            ReinforcementSendDefaults.NormalizeSendMaxMinutes(configuration.GetValue(
                BotOptionPayloadKeys.ReinforcementsSendMaxMinutes,
                ReinforcementSendDefaults.DefaultSendMaxMinutes)));

    internal static ReinforcementOptions Apply(
        BotOptions source,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ReinforcementOptions(
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

    internal static BotOptions ApplyTo(this ReinforcementOptions values, BotOptions source)
        => source with
        {
            ReinforcementsEnabled = values.Enabled,
            ReinforcementsTargetVillageName = values.TargetVillageName,
            ReinforcementsSourceVillageNames = values.SourceVillageNames,
            ReinforcementsTroopRules = values.TroopRules,
            ReinforcementsSendMinMinutes = values.SendMinMinutes,
            ReinforcementsSendMaxMinutes = values.SendMaxMinutes,
        };

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

    private static List<ReinforcementTroopRule> NormalizeTroopRules(IEnumerable<ReinforcementTroopRule> rules)
        => rules
            .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.TroopType))
            .Select(rule => rule.Normalize())
            .GroupBy(rule => $"{rule.AccountName}\u001f{rule.SourceVillageName}\u001f{rule.TroopType}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
}
