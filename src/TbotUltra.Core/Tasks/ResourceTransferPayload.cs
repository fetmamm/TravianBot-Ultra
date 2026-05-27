using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

public sealed record ResourceTransferPayload(
    bool Enabled,
    string TargetVillageName,
    IReadOnlyList<string> SourceVillageNames,
    int SourceThresholdPercent,
    int SourceKeepPercent,
    int TargetFillPercent,
    bool SendWood,
    bool SendClay,
    bool SendIron,
    bool SendCrop)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out ResourceTransferPayload? result)
    {
        result = null;
        if (!TryReadBool(payload, BotOptionPayloadKeys.ResourceTransferEnabled, defaultValue: true, out var enabled)
            || !TryReadPercent(payload, BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent, 50, out var threshold)
            || !TryReadPercent(payload, BotOptionPayloadKeys.ResourceTransferSourceKeepPercent, 5, out var keep)
            || !TryReadPercent(payload, BotOptionPayloadKeys.ResourceTransferTargetFillPercent, 90, out var fill)
            || !TryReadBool(payload, BotOptionPayloadKeys.ResourceTransferSendWood, defaultValue: true, out var sendWood)
            || !TryReadBool(payload, BotOptionPayloadKeys.ResourceTransferSendClay, defaultValue: true, out var sendClay)
            || !TryReadBool(payload, BotOptionPayloadKeys.ResourceTransferSendIron, defaultValue: true, out var sendIron)
            || !TryReadBool(payload, BotOptionPayloadKeys.ResourceTransferSendCrop, defaultValue: true, out var sendCrop))
        {
            return false;
        }

        var target = ReadTrimmed(payload, BotOptionPayloadKeys.ResourceTransferTargetVillageName) ?? string.Empty;
        var sources = ParseNames(ReadTrimmed(payload, BotOptionPayloadKeys.ResourceTransferSourceVillageNames));
        result = new ResourceTransferPayload(enabled, target, sources, threshold, keep, fill, sendWood, sendClay, sendIron, sendCrop);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceTransferEnabled] = Enabled ? "true" : "false",
            [BotOptionPayloadKeys.ResourceTransferTargetVillageName] = TargetVillageName.Trim(),
            [BotOptionPayloadKeys.ResourceTransferSourceVillageNames] = string.Join(",", SourceVillageNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
            [BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent] = SourceThresholdPercent.ToString(),
            [BotOptionPayloadKeys.ResourceTransferSourceKeepPercent] = SourceKeepPercent.ToString(),
            [BotOptionPayloadKeys.ResourceTransferTargetFillPercent] = TargetFillPercent.ToString(),
            [BotOptionPayloadKeys.ResourceTransferSendWood] = SendWood ? "true" : "false",
            [BotOptionPayloadKeys.ResourceTransferSendClay] = SendClay ? "true" : "false",
            [BotOptionPayloadKeys.ResourceTransferSendIron] = SendIron ? "true" : "false",
            [BotOptionPayloadKeys.ResourceTransferSendCrop] = SendCrop ? "true" : "false",
        };
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, string> payload, string key, bool defaultValue, out bool value)
    {
        value = defaultValue;
        return !payload.TryGetValue(key, out var raw) || bool.TryParse(raw, out value);
    }

    private static bool TryReadPercent(IReadOnlyDictionary<string, string> payload, string key, int defaultValue, out int value)
    {
        value = defaultValue;
        if (!payload.TryGetValue(key, out var raw))
        {
            return true;
        }

        if (!int.TryParse(raw, out var parsed))
        {
            return false;
        }

        value = Math.Clamp(parsed, 0, 100);
        return true;
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
