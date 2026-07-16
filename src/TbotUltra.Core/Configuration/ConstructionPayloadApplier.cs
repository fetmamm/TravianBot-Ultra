namespace TbotUltra.Core.Configuration;

internal sealed record ConstructionPayloadValues(
    string TargetVillageName,
    string TargetVillageUrl,
    int? ResourceUpgradeSlotId,
    int? ResourceUpgradeTargetLevel,
    int ResourceUpgradeMaxAttempts,
    string ResourceBuildStrategy,
    string? SmithyUpgradeTargets,
    int? BuildingUpgradeSlotId,
    int? BuildingUpgradeTargetLevel,
    int BuildingUpgradeMaxAttempts,
    int? BuildingConstructSlotId,
    int? BuildingConstructGid,
    string BuildingConstructName,
    bool BuildingConstructAllowSlotFallback,
    string BuildingConstructFallbackExcludedSlots,
    bool ConstructFasterEnabled,
    bool ConstructFasterMinBuildTimeEnabled,
    int ConstructFasterMinBuildMinutes,
    bool ConstructFasterRandomEnabled,
    int ConstructFasterRandomChancePercent,
    string TargetBuildingSlotOrName,
    int? TargetLevel,
    string UpgradeSelectorProfile,
    bool ConstructionPreSleepFill);

internal static class ConstructionPayloadApplier
{
    internal static ConstructionPayloadValues Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ConstructionPayloadValues(
            source.TargetVillageName,
            source.TargetVillageUrl,
            source.ResourceUpgradeSlotId,
            source.ResourceUpgradeTargetLevel,
            source.ResourceUpgradeMaxAttempts,
            source.ResourceBuildStrategy,
            source.SmithyUpgradeTargets,
            source.BuildingUpgradeSlotId,
            source.BuildingUpgradeTargetLevel,
            source.BuildingUpgradeMaxAttempts,
            source.BuildingConstructSlotId,
            source.BuildingConstructGid,
            source.BuildingConstructName,
            source.BuildingConstructAllowSlotFallback,
            source.BuildingConstructFallbackExcludedSlots,
            source.ConstructFasterEnabled,
            source.ConstructFasterMinBuildTimeEnabled,
            source.ConstructFasterMinBuildMinutes,
            source.ConstructFasterRandomEnabled,
            source.ConstructFasterRandomChancePercent,
            source.TargetBuildingSlotOrName,
            source.TargetLevel,
            source.UpgradeSelectorProfile,
            source.ConstructionPreSleepFill);

        if (payload is null)
            return result;

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;

            if (key.Equals(BotOptionPayloadKeys.TargetVillageName, StringComparison.OrdinalIgnoreCase))
                result = result with { TargetVillageName = value };
            else if (key.Equals(BotOptionPayloadKeys.TargetVillageUrl, StringComparison.OrdinalIgnoreCase))
                result = result with { TargetVillageUrl = value };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceUpgradeSlotId, out var resourceSlot))
                result = result with { ResourceUpgradeSlotId = resourceSlot };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var resourceTarget))
                result = result with { ResourceUpgradeTargetLevel = resourceTarget };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, out var resourceAttempts))
                result = result with { ResourceUpgradeMaxAttempts = resourceAttempts };
            else if (key.Equals(BotOptionPayloadKeys.ResourceBuildStrategy, StringComparison.OrdinalIgnoreCase))
                result = result with { ResourceBuildStrategy = value.Equals("smart", StringComparison.OrdinalIgnoreCase) ? "smart" : "lowest_first" };
            else if (key.Equals(BotOptionPayloadKeys.SmithyUpgradeTargets, StringComparison.OrdinalIgnoreCase))
                result = result with { SmithyUpgradeTargets = value };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingUpgradeSlotId, out var buildingSlot))
                result = result with { BuildingUpgradeSlotId = buildingSlot };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var buildingTarget))
                result = result with { BuildingUpgradeTargetLevel = buildingTarget };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingUpgradeMaxAttempts, out var buildingAttempts))
                result = result with { BuildingUpgradeMaxAttempts = buildingAttempts };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingConstructSlotId, out var constructSlot))
                result = result with { BuildingConstructSlotId = constructSlot };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingConstructGid, out var constructGid))
                result = result with { BuildingConstructGid = constructGid };
            else if (key.Equals(BotOptionPayloadKeys.BuildingConstructName, StringComparison.OrdinalIgnoreCase))
                result = result with { BuildingConstructName = value };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.BuildingConstructAllowSlotFallback, out var allowSlotFallback))
                result = result with { BuildingConstructAllowSlotFallback = allowSlotFallback };
            else if (key.Equals(BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots, StringComparison.OrdinalIgnoreCase))
                result = result with { BuildingConstructFallbackExcludedSlots = value };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ConstructFasterEnabled, out var faster))
                result = result with { ConstructFasterEnabled = faster };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled, out var minTimeEnabled))
                result = result with { ConstructFasterMinBuildTimeEnabled = minTimeEnabled };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ConstructFasterMinBuildMinutes, out var minMinutes))
                result = result with { ConstructFasterMinBuildMinutes = Math.Max(0, minMinutes) };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ConstructFasterRandomEnabled, out var randomEnabled))
                result = result with { ConstructFasterRandomEnabled = randomEnabled };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ConstructFasterRandomChancePercent, out var randomChance))
                result = result with { ConstructFasterRandomChancePercent = Math.Clamp(randomChance, 0, 100) };
            else if (key.Equals(BotOptionPayloadKeys.TargetBuildingSlotOrName, StringComparison.OrdinalIgnoreCase))
                result = result with { TargetBuildingSlotOrName = value };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.TargetLevel, out var targetLevel))
                result = result with { TargetLevel = targetLevel };
            else if (key.Equals(BotOptionPayloadKeys.UpgradeSelectorProfile, StringComparison.OrdinalIgnoreCase))
                result = result with { UpgradeSelectorProfile = value };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ConstructionPreSleepFill, out var preSleepFill))
                result = result with { ConstructionPreSleepFill = preSleepFill };
        }

        return result;
    }

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
