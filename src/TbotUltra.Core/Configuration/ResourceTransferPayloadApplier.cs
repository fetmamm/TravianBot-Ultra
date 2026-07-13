namespace TbotUltra.Core.Configuration;

internal sealed record ResourceTransferPayloadValues(
    bool Enabled,
    string TargetVillageName,
    List<string> SourceVillageNames,
    int SourceThresholdPercent,
    int SourceKeepPercent,
    int TargetFillPercent,
    bool SendWood,
    bool SendClay,
    bool SendIron,
    bool SendCrop);

internal static class ResourceTransferPayloadApplier
{
    internal static ResourceTransferPayloadValues Apply(
        BotOptions source,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ResourceTransferPayloadValues(
            source.ResourceTransferEnabled,
            source.ResourceTransferTargetVillageName,
            source.ResourceTransferSourceVillageNames,
            source.ResourceTransferSourceThresholdPercent,
            source.ResourceTransferSourceKeepPercent,
            source.ResourceTransferTargetFillPercent,
            source.ResourceTransferSendWood,
            source.ResourceTransferSendClay,
            source.ResourceTransferSendIron,
            source.ResourceTransferSendCrop);

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

            if (key.Equals(BotOptionPayloadKeys.ResourceTransferEnabled, StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(value, out var enabled))
            {
                result = result with { Enabled = enabled };
            }
            else if (key.Equals(BotOptionPayloadKeys.ResourceTransferTargetVillageName, StringComparison.OrdinalIgnoreCase))
            {
                result = result with { TargetVillageName = value };
            }
            else if (key.Equals(BotOptionPayloadKeys.ResourceTransferSourceVillageNames, StringComparison.OrdinalIgnoreCase))
            {
                result = result with { SourceVillageNames = ParseVillageNames(value) };
            }
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent, out var sourceThreshold))
            {
                result = result with { SourceThresholdPercent = Math.Clamp(sourceThreshold, 0, 100) };
            }
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceTransferSourceKeepPercent, out var sourceKeep))
            {
                result = result with { SourceKeepPercent = Math.Clamp(sourceKeep, 0, 99) };
            }
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceTransferTargetFillPercent, out var targetFill))
            {
                result = result with { TargetFillPercent = Math.Clamp(targetFill, 0, 100) };
            }
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ResourceTransferSendWood, out var sendWood))
            {
                result = result with { SendWood = sendWood };
            }
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ResourceTransferSendClay, out var sendClay))
            {
                result = result with { SendClay = sendClay };
            }
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ResourceTransferSendIron, out var sendIron))
            {
                result = result with { SendIron = sendIron };
            }
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ResourceTransferSendCrop, out var sendCrop))
            {
                result = result with { SendCrop = sendCrop };
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
