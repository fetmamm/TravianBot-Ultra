using Microsoft.Extensions.Configuration;
using static TbotUltra.Core.Configuration.PayloadValueReader;

namespace TbotUltra.Core.Configuration;

internal sealed record ResourceTransferOptions(
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

internal static class ResourceTransferOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.ResourceTransferEnabled,
        BotOptionPayloadKeys.ResourceTransferTargetVillageName,
        BotOptionPayloadKeys.ResourceTransferSourceVillageNames,
        BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent,
        BotOptionPayloadKeys.ResourceTransferSourceKeepPercent,
        BotOptionPayloadKeys.ResourceTransferTargetFillPercent,
        BotOptionPayloadKeys.ResourceTransferSendWood,
        BotOptionPayloadKeys.ResourceTransferSendClay,
        BotOptionPayloadKeys.ResourceTransferSendIron,
        BotOptionPayloadKeys.ResourceTransferSendCrop,
    ];

    internal static ResourceTransferOptions FromConfiguration(IConfiguration configuration)
        => new(
            configuration.GetValue(BotOptionPayloadKeys.ResourceTransferEnabled, false),
            configuration[BotOptionPayloadKeys.ResourceTransferTargetVillageName] ?? string.Empty,
            configuration.GetSection(BotOptionPayloadKeys.ResourceTransferSourceVillageNames).Get<List<string>>() ?? [],
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent, 50), 0, 100),
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSourceKeepPercent, 5), 0, 99),
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ResourceTransferTargetFillPercent, 90), 0, 100),
            configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendWood, true),
            configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendClay, true),
            configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendIron, true),
            configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendCrop, true));

    internal static ResourceTransferOptions Apply(
        BotOptions source,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ResourceTransferOptions(
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
                result = result with { SourceVillageNames = VillageNameListPayloadCodec.Parse(value) };
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

    internal static BotOptions ApplyTo(this ResourceTransferOptions values, BotOptions source)
        => source with
        {
            ResourceTransferEnabled = values.Enabled,
            ResourceTransferTargetVillageName = values.TargetVillageName,
            ResourceTransferSourceVillageNames = values.SourceVillageNames,
            ResourceTransferSourceThresholdPercent = values.SourceThresholdPercent,
            ResourceTransferSourceKeepPercent = values.SourceKeepPercent,
            ResourceTransferTargetFillPercent = values.TargetFillPercent,
            ResourceTransferSendWood = values.SendWood,
            ResourceTransferSendClay = values.SendClay,
            ResourceTransferSendIron = values.SendIron,
            ResourceTransferSendCrop = values.SendCrop,
        };

}
