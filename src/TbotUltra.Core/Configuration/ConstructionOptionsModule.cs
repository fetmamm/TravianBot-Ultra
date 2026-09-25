using Microsoft.Extensions.Configuration;
using static TbotUltra.Core.Configuration.PayloadValueReader;

namespace TbotUltra.Core.Configuration;

internal sealed record ConstructionOptions(
    string TargetVillageName,
    string TargetVillageUrl,
    string TargetVillageKey,
    int? ResourceUpgradeSlotId,
    int? ResourceUpgradeTargetLevel,
    int ResourceUpgradeMaxAttempts,
    string ResourceBuildStrategy,
    string ResourceUpgradeTypes,
    string ResourceQueuedLevelProjections,
    string? SmithyUpgradeTargets,
    int? BuildingUpgradeSlotId,
    int? BuildingUpgradeTargetLevel,
    string BuildingUpgradeName,
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
    bool ConstructionPreSleepFill,
    bool ConstructionLoginFill,
    long? ConstructionLoginFillExpiresAtUnixSeconds,
    bool ConstructionHumanizePreNavigationDelaySatisfied)
{
    public bool SmithyRestartDelayEnabled { get; init; }
    public double SmithyRestartDelayMinMinutes { get; init; }
    public double SmithyRestartDelayMaxMinutes { get; init; }
    public bool HumanizeDelayEnabled { get; init; }
    public bool MainBuildingRebuildEnabled { get; init; }
    public int MainBuildingRebuildTargetLevel { get; init; }
    public int StorageUpgradeLevelsAhead { get; init; }
    public bool CropShortageRecoveryEnabled { get; init; }
    public int HumanizeStateVersion { get; init; }
    public double HumanizeQueuePercentMin { get; init; }
    public double HumanizeQueuePercentMax { get; init; }
    public double HumanizeMaxDelayMinutes { get; init; }
    public double HumanizeNoPlusMinMinutes { get; init; }
    public double HumanizeNoPlusMaxMinutes { get; init; }
    public int DemolishDelayMinMinutes { get; init; }
    public int DemolishDelayMaxMinutes { get; init; }
}

internal static class ConstructionOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.TargetVillageName,
        BotOptionPayloadKeys.TargetVillageUrl,
        BotOptionPayloadKeys.ResourceUpgradeSlotId,
        BotOptionPayloadKeys.ResourceUpgradeTargetLevel,
        BotOptionPayloadKeys.ResourceUpgradeMaxAttempts,
        BotOptionPayloadKeys.ResourceBuildStrategy,
        BotOptionPayloadKeys.BuildingUpgradeSlotId,
        BotOptionPayloadKeys.BuildingUpgradeTargetLevel,
        BotOptionPayloadKeys.BuildingUpgradeMaxAttempts,
        BotOptionPayloadKeys.BuildingConstructSlotId,
        BotOptionPayloadKeys.BuildingConstructGid,
        BotOptionPayloadKeys.BuildingConstructName,
        BotOptionPayloadKeys.ConstructFasterEnabled,
        BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled,
        BotOptionPayloadKeys.ConstructFasterMinBuildMinutes,
        BotOptionPayloadKeys.ConstructFasterRandomEnabled,
        BotOptionPayloadKeys.ConstructFasterRandomChancePercent,
        BotOptionPayloadKeys.TargetBuildingSlotOrName,
        BotOptionPayloadKeys.TargetLevel,
        BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled,
        BotOptionPayloadKeys.ConstructionMainBuildingRebuildEnabled,
        BotOptionPayloadKeys.ConstructionMainBuildingRebuildTargetLevel,
        BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead,
        BotOptionPayloadKeys.ConstructionCropShortageRecoveryEnabled,
        BotOptionPayloadKeys.ConstructionHumanizeStateVersion,
        BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin,
        BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax,
        BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes,
        BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes,
        BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes,
        BotOptionPayloadKeys.UpgradeSelectorProfile,
    ];

    internal static ConstructionOptions FromConfiguration(IConfiguration configuration)
        => new(
            configuration[BotOptionPayloadKeys.TargetVillageName] ?? string.Empty,
            configuration[BotOptionPayloadKeys.TargetVillageUrl] ?? string.Empty,
            configuration[BotOptionPayloadKeys.TargetVillageKey] ?? string.Empty,
            configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeSlotId),
            configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeTargetLevel),
            configuration.GetValue(BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, 30),
            configuration[BotOptionPayloadKeys.ResourceBuildStrategy] ?? "lowest_first",
            "wood,clay,iron,crop",
            string.Empty,
            configuration[BotOptionPayloadKeys.SmithyUpgradeTargets],
            configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeSlotId),
            configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeTargetLevel),
            configuration[BotOptionPayloadKeys.BuildingUpgradeName] ?? string.Empty,
            configuration.GetValue(BotOptionPayloadKeys.BuildingUpgradeMaxAttempts, 30),
            configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructSlotId),
            configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructGid),
            configuration[BotOptionPayloadKeys.BuildingConstructName] ?? string.Empty,
            configuration.GetValue(BotOptionPayloadKeys.BuildingConstructAllowSlotFallback, false),
            configuration[BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots] ?? string.Empty,
            configuration.GetValue(BotOptionPayloadKeys.ConstructFasterEnabled, true),
            configuration.GetValue(BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled, true),
            Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes, 30)),
            configuration.GetValue(BotOptionPayloadKeys.ConstructFasterRandomEnabled, false),
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ConstructFasterRandomChancePercent, 50), 0, 100),
            configuration[BotOptionPayloadKeys.TargetBuildingSlotOrName] ?? string.Empty,
            configuration.GetValue<int?>(BotOptionPayloadKeys.TargetLevel),
            configuration[BotOptionPayloadKeys.UpgradeSelectorProfile] ?? "auto",
            configuration.GetValue(BotOptionPayloadKeys.ConstructionPreSleepFill, false),
            configuration.GetValue(BotOptionPayloadKeys.ConstructionLoginFill, false),
            configuration.GetValue<long?>(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds),
            configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied, false))
        {
            SmithyRestartDelayEnabled = configuration.GetValue(
                BotOptionPayloadKeys.SmithyUpgradeRestartDelayEnabled,
                SmithyUpgradeRestartDelayDefaults.Enabled),
            SmithyRestartDelayMinMinutes = configuration.GetValue(
                BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes,
                SmithyUpgradeRestartDelayDefaults.MinMinutes),
            SmithyRestartDelayMaxMinutes = configuration.GetValue(
                BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes,
                SmithyUpgradeRestartDelayDefaults.MaxMinutes),
            HumanizeDelayEnabled = configuration.GetValue(
                BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled,
                PacingDefaults.ConstructionHumanizeDelayEnabled),
            MainBuildingRebuildEnabled = configuration.GetValue(
                BotOptionPayloadKeys.ConstructionMainBuildingRebuildEnabled,
                ConstructionDefaults.MainBuildingRebuildEnabled),
            MainBuildingRebuildTargetLevel = ConstructionDefaults.NormalizeMainBuildingRebuildTargetLevel(configuration.GetValue(
                BotOptionPayloadKeys.ConstructionMainBuildingRebuildTargetLevel,
                ConstructionDefaults.MainBuildingRebuildTargetLevel)),
            StorageUpgradeLevelsAhead = ConstructionDefaults.NormalizeStorageUpgradeLevelsAhead(configuration.GetValue(
                BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead,
                ConstructionDefaults.StorageUpgradeLevelsAhead)),
            CropShortageRecoveryEnabled = configuration.GetValue(
                BotOptionPayloadKeys.ConstructionCropShortageRecoveryEnabled,
                ConstructionDefaults.CropShortageRecoveryEnabled),
            HumanizeStateVersion = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeStateVersion, 0),
            HumanizeQueuePercentMin = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin, PacingDefaults.ConstructionHumanizeQueuePercentMin),
            HumanizeQueuePercentMax = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax, PacingDefaults.ConstructionHumanizeQueuePercentMax),
            HumanizeMaxDelayMinutes = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes, PacingDefaults.ConstructionHumanizeMaxDelayMinutes),
            HumanizeNoPlusMinMinutes = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes, PacingDefaults.ConstructionHumanizeNoPlusMinMinutes),
            HumanizeNoPlusMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes, PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes),
            DemolishDelayMinMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.DemolishDelayMinMinutes, DemolishDefaults.DefaultDelayMinMinutes), 0, 1440),
            DemolishDelayMaxMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.DemolishDelayMaxMinutes, DemolishDefaults.DefaultDelayMaxMinutes), 0, 1440),
        };

    internal static ConstructionOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ConstructionOptions(
            source.TargetVillageName,
            source.TargetVillageUrl,
            source.TargetVillageKey,
            source.ResourceUpgradeSlotId,
            source.ResourceUpgradeTargetLevel,
            source.ResourceUpgradeMaxAttempts,
            source.ResourceBuildStrategy,
            source.ResourceUpgradeTypes,
            source.ResourceQueuedLevelProjections,
            source.SmithyUpgradeTargets,
            source.BuildingUpgradeSlotId,
            source.BuildingUpgradeTargetLevel,
            source.BuildingUpgradeName,
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
            source.ConstructionPreSleepFill,
            source.ConstructionLoginFill,
            source.ConstructionLoginFillExpiresAtUnixSeconds,
            source.ConstructionHumanizePreNavigationDelaySatisfied)
        {
            SmithyRestartDelayEnabled = source.SmithyUpgradeRestartDelayEnabled,
            SmithyRestartDelayMinMinutes = source.SmithyUpgradeRestartDelayMinMinutes,
            SmithyRestartDelayMaxMinutes = source.SmithyUpgradeRestartDelayMaxMinutes,
            HumanizeDelayEnabled = source.ConstructionHumanizeDelayEnabled,
            MainBuildingRebuildEnabled = source.ConstructionMainBuildingRebuildEnabled,
            MainBuildingRebuildTargetLevel = source.ConstructionMainBuildingRebuildTargetLevel,
            StorageUpgradeLevelsAhead = source.ConstructionStorageUpgradeLevelsAhead,
            CropShortageRecoveryEnabled = source.ConstructionCropShortageRecoveryEnabled,
            HumanizeStateVersion = source.ConstructionHumanizeStateVersion,
            HumanizeQueuePercentMin = source.ConstructionHumanizeQueuePercentMin,
            HumanizeQueuePercentMax = source.ConstructionHumanizeQueuePercentMax,
            HumanizeMaxDelayMinutes = source.ConstructionHumanizeMaxDelayMinutes,
            HumanizeNoPlusMinMinutes = source.ConstructionHumanizeNoPlusMinMinutes,
            HumanizeNoPlusMaxMinutes = source.ConstructionHumanizeNoPlusMaxMinutes,
            DemolishDelayMinMinutes = source.DemolishDelayMinMinutes,
            DemolishDelayMaxMinutes = source.DemolishDelayMaxMinutes,
        };

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
            else if (key.Equals(BotOptionPayloadKeys.TargetVillageKey, StringComparison.OrdinalIgnoreCase))
                result = result with { TargetVillageKey = value };
            else if (PayloadValueReader.TryReadInt(key, value, BotOptionPayloadKeys.ResourceUpgradeSlotId, out var resourceSlot))
                result = result with { ResourceUpgradeSlotId = resourceSlot };
            else if (PayloadValueReader.TryReadInt(key, value, BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var resourceTarget))
                result = result with { ResourceUpgradeTargetLevel = resourceTarget };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, out var resourceAttempts))
                result = result with { ResourceUpgradeMaxAttempts = resourceAttempts };
            else if (key.Equals(BotOptionPayloadKeys.ResourceBuildStrategy, StringComparison.OrdinalIgnoreCase))
                result = result with { ResourceBuildStrategy = value.Equals("smart", StringComparison.OrdinalIgnoreCase) ? "smart" : "lowest_first" };
            else if (key.Equals(BotOptionPayloadKeys.ResourceUpgradeTypes, StringComparison.OrdinalIgnoreCase))
                result = result with { ResourceUpgradeTypes = value };
            else if (key.Equals(BotOptionPayloadKeys.ResourceQueuedLevelProjections, StringComparison.OrdinalIgnoreCase))
                result = result with { ResourceQueuedLevelProjections = value };
            else if (key.Equals(BotOptionPayloadKeys.SmithyUpgradeTargets, StringComparison.OrdinalIgnoreCase))
                result = result with { SmithyUpgradeTargets = value };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingUpgradeSlotId, out var buildingSlot))
                result = result with { BuildingUpgradeSlotId = buildingSlot };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var buildingTarget))
                result = result with { BuildingUpgradeTargetLevel = buildingTarget };
            else if (key.Equals(BotOptionPayloadKeys.BuildingUpgradeName, StringComparison.OrdinalIgnoreCase))
                result = result with { BuildingUpgradeName = value };
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
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ConstructionLoginFill, out var loginFill))
                result = result with { ConstructionLoginFill = loginFill };
            else if (TryReadLong(key, value, BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds, out var loginFillExpiry))
                result = result with { ConstructionLoginFillExpiresAtUnixSeconds = loginFillExpiry };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied, out var preNavigationDelaySatisfied))
                result = result with { ConstructionHumanizePreNavigationDelaySatisfied = preNavigationDelaySatisfied };
        }

        return result;
    }

    internal static BotOptions ApplyTo(this ConstructionOptions values, BotOptions source)
        => source with
        {
            TargetVillageName = values.TargetVillageName,
            TargetVillageUrl = values.TargetVillageUrl,
            TargetVillageKey = values.TargetVillageKey,
            ResourceUpgradeSlotId = values.ResourceUpgradeSlotId,
            ResourceUpgradeTargetLevel = values.ResourceUpgradeTargetLevel,
            ResourceUpgradeMaxAttempts = values.ResourceUpgradeMaxAttempts,
            ResourceBuildStrategy = values.ResourceBuildStrategy,
            ResourceUpgradeTypes = values.ResourceUpgradeTypes,
            ResourceQueuedLevelProjections = values.ResourceQueuedLevelProjections,
            SmithyUpgradeTargets = values.SmithyUpgradeTargets,
            BuildingUpgradeSlotId = values.BuildingUpgradeSlotId,
            BuildingUpgradeTargetLevel = values.BuildingUpgradeTargetLevel,
            BuildingUpgradeName = values.BuildingUpgradeName,
            BuildingUpgradeMaxAttempts = values.BuildingUpgradeMaxAttempts,
            BuildingConstructSlotId = values.BuildingConstructSlotId,
            BuildingConstructGid = values.BuildingConstructGid,
            BuildingConstructName = values.BuildingConstructName,
            BuildingConstructAllowSlotFallback = values.BuildingConstructAllowSlotFallback,
            BuildingConstructFallbackExcludedSlots = values.BuildingConstructFallbackExcludedSlots,
            ConstructFasterEnabled = values.ConstructFasterEnabled,
            ConstructFasterMinBuildTimeEnabled = values.ConstructFasterMinBuildTimeEnabled,
            ConstructFasterMinBuildMinutes = values.ConstructFasterMinBuildMinutes,
            ConstructFasterRandomEnabled = values.ConstructFasterRandomEnabled,
            ConstructFasterRandomChancePercent = values.ConstructFasterRandomChancePercent,
            TargetBuildingSlotOrName = values.TargetBuildingSlotOrName,
            TargetLevel = values.TargetLevel,
            UpgradeSelectorProfile = values.UpgradeSelectorProfile,
            ConstructionPreSleepFill = values.ConstructionPreSleepFill,
            ConstructionLoginFill = values.ConstructionLoginFill,
            ConstructionLoginFillExpiresAtUnixSeconds = values.ConstructionLoginFillExpiresAtUnixSeconds,
            ConstructionHumanizePreNavigationDelaySatisfied = values.ConstructionHumanizePreNavigationDelaySatisfied,
            SmithyUpgradeRestartDelayEnabled = values.SmithyRestartDelayEnabled,
            SmithyUpgradeRestartDelayMinMinutes = values.SmithyRestartDelayMinMinutes,
            SmithyUpgradeRestartDelayMaxMinutes = values.SmithyRestartDelayMaxMinutes,
            ConstructionHumanizeDelayEnabled = values.HumanizeDelayEnabled,
            ConstructionMainBuildingRebuildEnabled = values.MainBuildingRebuildEnabled,
            ConstructionMainBuildingRebuildTargetLevel = values.MainBuildingRebuildTargetLevel,
            ConstructionStorageUpgradeLevelsAhead = values.StorageUpgradeLevelsAhead,
            ConstructionCropShortageRecoveryEnabled = values.CropShortageRecoveryEnabled,
            ConstructionHumanizeStateVersion = values.HumanizeStateVersion,
            ConstructionHumanizeQueuePercentMin = values.HumanizeQueuePercentMin,
            ConstructionHumanizeQueuePercentMax = values.HumanizeQueuePercentMax,
            ConstructionHumanizeMaxDelayMinutes = values.HumanizeMaxDelayMinutes,
            ConstructionHumanizeNoPlusMinMinutes = values.HumanizeNoPlusMinMinutes,
            ConstructionHumanizeNoPlusMaxMinutes = values.HumanizeNoPlusMaxMinutes,
            DemolishDelayMinMinutes = values.DemolishDelayMinMinutes,
            DemolishDelayMaxMinutes = values.DemolishDelayMaxMinutes,
        };

}
