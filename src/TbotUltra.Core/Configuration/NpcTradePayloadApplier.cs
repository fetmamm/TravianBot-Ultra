namespace TbotUltra.Core.Configuration;

internal sealed record NpcTradePayloadValues(
    bool Enabled,
    bool ConstructionEnabled,
    int ThresholdPercent,
    bool AnalyzeWood,
    bool AnalyzeClay,
    bool AnalyzeIron,
    bool AnalyzeCrop,
    bool BuildTimeLimitEnabled,
    int BuildTimeLimitSeconds);

internal static class NpcTradePayloadApplier
{
    internal static NpcTradePayloadValues Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new NpcTradePayloadValues(
            source.NpcTradeEnabled,
            source.NpcTradeConstructionEnabled,
            source.NpcTradeThresholdPercent,
            source.NpcTradeAnalyzeWood,
            source.NpcTradeAnalyzeClay,
            source.NpcTradeAnalyzeIron,
            source.NpcTradeAnalyzeCrop,
            source.NpcTradeBuildTimeLimitEnabled,
            source.NpcTradeBuildTimeLimitSeconds);

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

            if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeEnabled, out var enabled))
                result = result with { Enabled = enabled };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeConstructionEnabled, out var construction))
                result = result with { ConstructionEnabled = construction };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.NpcTradeThresholdPercent, out var threshold))
                result = result with { ThresholdPercent = Math.Clamp(threshold, 1, 100) };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeAnalyzeWood, out var wood))
                result = result with { AnalyzeWood = wood };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeAnalyzeClay, out var clay))
                result = result with { AnalyzeClay = clay };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeAnalyzeIron, out var iron))
                result = result with { AnalyzeIron = iron };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeAnalyzeCrop, out var crop))
                result = result with { AnalyzeCrop = crop };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled, out var timeLimitEnabled))
                result = result with { BuildTimeLimitEnabled = timeLimitEnabled };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds, out var timeLimit))
                result = result with { BuildTimeLimitSeconds = NormalizeBuildTimeLimit(timeLimit) };
        }

        return result;
    }

    private static int NormalizeBuildTimeLimit(int value)
        => value is 30 or 60 or 300 or 1200 or 3600 ? value : 60;

    private static bool TryReadInt(string key, string value, string expectedKey, out int parsed)
    {
        parsed = 0;
        return key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsed);
    }

    private static bool TryReadBool(string key, string value, string expectedKey, out bool parsed)
    {
        parsed = false;
        return key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsed);
    }
}
