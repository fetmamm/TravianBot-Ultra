namespace TbotUltra.Core.Configuration;

internal sealed record FarmingPayloadValues(
    List<string> ListNames,
    List<string> ListIds,
    int DispatchDelayMinMinutes,
    int DispatchDelayMaxMinutes,
    string SendMode,
    string TownHallCelebrationMode,
    bool DeactivateLosses,
    bool DeactivateOasisLosses,
    int NextListIndex);

internal static class FarmingPayloadApplier
{
    internal static FarmingPayloadValues Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new FarmingPayloadValues(
            source.ContinuousFarmListNames,
            source.ContinuousFarmListIds,
            source.ContinuousFarmDispatchDelayMinMinutes,
            source.ContinuousFarmDispatchDelayMaxMinutes,
            source.ContinuousFarmSendMode,
            source.TownHallCelebrationMode,
            source.ContinuousFarmDeactivateLosses,
            source.ContinuousFarmDeactivateOasisLosses,
            source.ContinuousFarmNextListIndex);

        if (payload is null)
            return result;

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;

            if (key.Equals(BotOptionPayloadKeys.ContinuousFarmListNames, StringComparison.OrdinalIgnoreCase))
                result = result with { ListNames = ParseList(value) };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmListIds, StringComparison.OrdinalIgnoreCase))
                result = result with { ListIds = ParseList(value) };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes, out var delayMin))
                result = result with { DispatchDelayMinMinutes = FarmingDefaults.NormalizeDispatchDelayMinMinutes(delayMin) };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes, out var delayMax))
                result = result with { DispatchDelayMaxMinutes = FarmingDefaults.NormalizeDispatchDelayMaxMinutes(delayMax) };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmSendMode, StringComparison.OrdinalIgnoreCase))
                result = result with { SendMode = FarmingDefaults.NormalizeSendMode(value) };
            else if (key.Equals(BotOptionPayloadKeys.TownHallCelebrationMode, StringComparison.OrdinalIgnoreCase))
                result = result with { TownHallCelebrationMode = TownHallCelebrationDefaults.NormalizeMode(value) };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmDeactivateLosses, out var losses))
                result = result with { DeactivateLosses = losses };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses, out var oasisLosses))
                result = result with { DeactivateOasisLosses = oasisLosses };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ContinuousFarmNextListIndex, out var nextIndex))
                result = result with { NextListIndex = Math.Max(0, nextIndex) };
        }

        return result;
    }

    private static List<string> ParseList(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool TryReadInt(string key, string value, string expected, out int parsed)
    {
        parsed = 0;
        return key.Equals(expected, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsed);
    }

    private static bool TryReadBool(string key, string value, string expected, out bool parsed)
    {
        parsed = false;
        return key.Equals(expected, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsed);
    }
}
