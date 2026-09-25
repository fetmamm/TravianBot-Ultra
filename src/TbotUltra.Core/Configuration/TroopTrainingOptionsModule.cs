using Microsoft.Extensions.Configuration;
using static TbotUltra.Core.Configuration.PayloadValueReader;

namespace TbotUltra.Core.Configuration;

internal sealed record TroopTrainingBuildingOptions(
    bool Enabled,
    string TroopType,
    string MaxQueueHours,
    string AmountMode,
    int KeepResourcesPercent,
    string RunMode,
    int MinimumTroops,
    int MinimumResourcesPercent,
    int TimedMinMinutes,
    int TimedMaxMinutes,
    bool CheckWood,
    bool CheckClay,
    bool CheckIron,
    bool CheckCrop)
{
    public bool MinimumTroopsEnabled { get; init; }
    public int MaximumMinimumTroops { get; init; } = 100;
    public bool AutomaticResourceSelection { get; init; }
}

internal sealed record TroopTrainingOptions(
    TroopTrainingBuildingOptions Barracks,
    TroopTrainingBuildingOptions Stable,
    TroopTrainingBuildingOptions Workshop,
    int FallbackCooldownSeconds,
    bool BreweryAutoCelebrationEnabled);

internal static class TroopTrainingOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.TroopTrainingBarracksEnabled,
        BotOptionPayloadKeys.TroopTrainingBarracksTroopType,
        BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours,
        BotOptionPayloadKeys.TroopTrainingBarracksAmountMode,
        BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingBarracksRunMode,
        BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroopsEnabled,
        BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingBarracksMaximumMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes,
        BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckWood,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckClay,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckIron,
        BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop,
        BotOptionPayloadKeys.TroopTrainingBarracksAutomaticResourceSelection,
        BotOptionPayloadKeys.TroopTrainingStableEnabled,
        BotOptionPayloadKeys.TroopTrainingStableTroopType,
        BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours,
        BotOptionPayloadKeys.TroopTrainingStableAmountMode,
        BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingStableRunMode,
        BotOptionPayloadKeys.TroopTrainingStableMinimumTroopsEnabled,
        BotOptionPayloadKeys.TroopTrainingStableMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingStableMaximumMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes,
        BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes,
        BotOptionPayloadKeys.TroopTrainingStableCheckWood,
        BotOptionPayloadKeys.TroopTrainingStableCheckClay,
        BotOptionPayloadKeys.TroopTrainingStableCheckIron,
        BotOptionPayloadKeys.TroopTrainingStableCheckCrop,
        BotOptionPayloadKeys.TroopTrainingStableAutomaticResourceSelection,
        BotOptionPayloadKeys.TroopTrainingWorkshopEnabled,
        BotOptionPayloadKeys.TroopTrainingWorkshopTroopType,
        BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours,
        BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode,
        BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingWorkshopRunMode,
        BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroopsEnabled,
        BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingWorkshopMaximumMinimumTroops,
        BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent,
        BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes,
        BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron,
        BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop,
        BotOptionPayloadKeys.TroopTrainingWorkshopAutomaticResourceSelection,
        BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds,
        BotOptionPayloadKeys.BreweryAutoCelebrationEnabled,
    ];

    internal static TroopTrainingOptions FromConfiguration(IConfiguration configuration)
        => new(
            ReadBuilding(configuration, BarracksKeys),
            ReadBuilding(configuration, StableKeys),
            ReadBuilding(configuration, WorkshopKeys),
            NormalizeFallbackCooldown(configuration.GetValue(
                BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds,
                120)),
            configuration.GetValue(BotOptionPayloadKeys.BreweryAutoCelebrationEnabled, false));

    internal static TroopTrainingOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var barracks = ApplyBuilding(CreateBarracks(source), BarracksKeys, payload);
        var stable = ApplyBuilding(CreateStable(source), StableKeys, payload);
        var workshop = ApplyBuilding(CreateWorkshop(source), WorkshopKeys, payload);
        var fallbackCooldown = source.TroopTrainingFallbackCooldownSeconds;
        var breweryCelebration = source.BreweryAutoCelebrationEnabled;

        if (payload is not null)
        {
            foreach (var pair in payload)
            {
                var key = pair.Key.Trim();
                var value = pair.Value.Trim();
                if (key.Length == 0 || value.Length == 0)
                    continue;

                if (key.Equals(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out var cooldown))
                {
                    fallbackCooldown = cooldown is 10 or 30 or 60 or 120 or 300 or 600 ? cooldown : 30;
                }
                else if (key.Equals(BotOptionPayloadKeys.BreweryAutoCelebrationEnabled, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var celebration))
                {
                    breweryCelebration = celebration;
                }
            }
        }

        return new TroopTrainingOptions(barracks, stable, workshop, fallbackCooldown, breweryCelebration);
    }

    internal static BotOptions ApplyTo(
        this TroopTrainingOptions values,
        BotOptions source,
        bool preserveAccountSettings = false)
        => source with
        {
            TroopTrainingBarracksEnabled = values.Barracks.Enabled,
            TroopTrainingBarracksTroopType = values.Barracks.TroopType,
            TroopTrainingBarracksMaxQueueHours = values.Barracks.MaxQueueHours,
            TroopTrainingBarracksAmountMode = values.Barracks.AmountMode,
            TroopTrainingBarracksKeepResourcesPercent = values.Barracks.KeepResourcesPercent,
            TroopTrainingBarracksRunMode = values.Barracks.RunMode,
            TroopTrainingBarracksMinimumTroopsEnabled = values.Barracks.MinimumTroopsEnabled,
            TroopTrainingBarracksMinimumTroops = values.Barracks.MinimumTroops,
            TroopTrainingBarracksMaximumMinimumTroops = values.Barracks.MaximumMinimumTroops,
            TroopTrainingBarracksMinimumResourcesPercent = values.Barracks.MinimumResourcesPercent,
            TroopTrainingBarracksTimedMinMinutes = values.Barracks.TimedMinMinutes,
            TroopTrainingBarracksTimedMaxMinutes = values.Barracks.TimedMaxMinutes,
            TroopTrainingBarracksCheckWood = values.Barracks.CheckWood,
            TroopTrainingBarracksCheckClay = values.Barracks.CheckClay,
            TroopTrainingBarracksCheckIron = values.Barracks.CheckIron,
            TroopTrainingBarracksCheckCrop = values.Barracks.CheckCrop,
            TroopTrainingBarracksAutomaticResourceSelection = values.Barracks.AutomaticResourceSelection,
            TroopTrainingStableEnabled = values.Stable.Enabled,
            TroopTrainingStableTroopType = values.Stable.TroopType,
            TroopTrainingStableMaxQueueHours = values.Stable.MaxQueueHours,
            TroopTrainingStableAmountMode = values.Stable.AmountMode,
            TroopTrainingStableKeepResourcesPercent = values.Stable.KeepResourcesPercent,
            TroopTrainingStableRunMode = values.Stable.RunMode,
            TroopTrainingStableMinimumTroopsEnabled = values.Stable.MinimumTroopsEnabled,
            TroopTrainingStableMinimumTroops = values.Stable.MinimumTroops,
            TroopTrainingStableMaximumMinimumTroops = values.Stable.MaximumMinimumTroops,
            TroopTrainingStableMinimumResourcesPercent = values.Stable.MinimumResourcesPercent,
            TroopTrainingStableTimedMinMinutes = values.Stable.TimedMinMinutes,
            TroopTrainingStableTimedMaxMinutes = values.Stable.TimedMaxMinutes,
            TroopTrainingStableCheckWood = values.Stable.CheckWood,
            TroopTrainingStableCheckClay = values.Stable.CheckClay,
            TroopTrainingStableCheckIron = values.Stable.CheckIron,
            TroopTrainingStableCheckCrop = values.Stable.CheckCrop,
            TroopTrainingStableAutomaticResourceSelection = values.Stable.AutomaticResourceSelection,
            TroopTrainingWorkshopEnabled = values.Workshop.Enabled,
            TroopTrainingWorkshopTroopType = values.Workshop.TroopType,
            TroopTrainingWorkshopMaxQueueHours = values.Workshop.MaxQueueHours,
            TroopTrainingWorkshopAmountMode = values.Workshop.AmountMode,
            TroopTrainingWorkshopKeepResourcesPercent = values.Workshop.KeepResourcesPercent,
            TroopTrainingWorkshopRunMode = values.Workshop.RunMode,
            TroopTrainingWorkshopMinimumTroopsEnabled = values.Workshop.MinimumTroopsEnabled,
            TroopTrainingWorkshopMinimumTroops = values.Workshop.MinimumTroops,
            TroopTrainingWorkshopMaximumMinimumTroops = values.Workshop.MaximumMinimumTroops,
            TroopTrainingWorkshopMinimumResourcesPercent = values.Workshop.MinimumResourcesPercent,
            TroopTrainingWorkshopTimedMinMinutes = values.Workshop.TimedMinMinutes,
            TroopTrainingWorkshopTimedMaxMinutes = values.Workshop.TimedMaxMinutes,
            TroopTrainingWorkshopCheckWood = values.Workshop.CheckWood,
            TroopTrainingWorkshopCheckClay = values.Workshop.CheckClay,
            TroopTrainingWorkshopCheckIron = values.Workshop.CheckIron,
            TroopTrainingWorkshopCheckCrop = values.Workshop.CheckCrop,
            TroopTrainingWorkshopAutomaticResourceSelection = values.Workshop.AutomaticResourceSelection,
            TroopTrainingFallbackCooldownSeconds = preserveAccountSettings
                ? source.TroopTrainingFallbackCooldownSeconds
                : values.FallbackCooldownSeconds,
            BreweryAutoCelebrationEnabled = values.BreweryAutoCelebrationEnabled,
        };

    private static TroopTrainingBuildingOptions ApplyBuilding(
        TroopTrainingBuildingOptions source,
        BuildingKeys keys,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = source;
        if (payload is null)
            return result;

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;

            if (TryReadBool(key, value, keys.Enabled, out var enabled))
                result = result with { Enabled = enabled };
            else if (key.Equals(keys.TroopType, StringComparison.OrdinalIgnoreCase))
                result = result with { TroopType = value };
            else if (key.Equals(keys.MaxQueueHours, StringComparison.OrdinalIgnoreCase))
                result = result with { MaxQueueHours = value };
            else if (key.Equals(keys.AmountMode, StringComparison.OrdinalIgnoreCase))
                result = result with { AmountMode = value };
            else if (TryReadInt(key, value, keys.KeepResourcesPercent, out var keepPercent))
                result = result with { KeepResourcesPercent = Math.Clamp(keepPercent, 0, 95) };
            else if (key.Equals(keys.RunMode, StringComparison.OrdinalIgnoreCase))
                result = result with { RunMode = NormalizeRunMode(value) };
            else if (TryReadBool(key, value, keys.MinimumTroopsEnabled, out var minimumTroopsEnabled))
                result = result with { MinimumTroopsEnabled = minimumTroopsEnabled };
            else if (TryReadInt(key, value, keys.MinimumTroops, out var minimumTroops))
                result = result with { MinimumTroops = Math.Clamp(minimumTroops, 1, 10000) };
            else if (TryReadInt(key, value, keys.MaximumMinimumTroops, out var maximumMinimumTroops))
                result = result with { MaximumMinimumTroops = Math.Clamp(maximumMinimumTroops, 1, 10000) };
            else if (TryReadInt(key, value, keys.MinimumResourcesPercent, out var minimumResources))
                result = result with { MinimumResourcesPercent = Math.Clamp(minimumResources, 0, 100) };
            else if (TryReadInt(key, value, keys.TimedMinMinutes, out var timedMin))
                result = result with { TimedMinMinutes = Math.Max(1, timedMin) };
            else if (TryReadInt(key, value, keys.TimedMaxMinutes, out var timedMax))
                result = result with { TimedMaxMinutes = Math.Max(1, timedMax) };
            else if (TryReadBool(key, value, keys.CheckWood, out var wood))
                result = result with { CheckWood = wood };
            else if (TryReadBool(key, value, keys.CheckClay, out var clay))
                result = result with { CheckClay = clay };
            else if (TryReadBool(key, value, keys.CheckIron, out var iron))
                result = result with { CheckIron = iron };
            else if (TryReadBool(key, value, keys.CheckCrop, out var crop))
                result = result with { CheckCrop = crop };
            else if (TryReadBool(key, value, keys.AutomaticResourceSelection, out var automaticResourceSelection))
                result = result with { AutomaticResourceSelection = automaticResourceSelection };
        }

        return result;
    }

    private static TroopTrainingBuildingOptions ReadBuilding(
        IConfiguration configuration,
        BuildingKeys keys)
        => new(
            configuration.GetValue(keys.Enabled, false),
            configuration[keys.TroopType] ?? string.Empty,
            configuration[keys.MaxQueueHours] ?? "no_limit",
            configuration[keys.AmountMode] ?? "maximum",
            Math.Clamp(configuration.GetValue(keys.KeepResourcesPercent, 10), 0, 95),
            NormalizeConfiguredRunMode(configuration[keys.RunMode]),
            Math.Clamp(configuration.GetValue(keys.MinimumTroops, 20), 1, 10000),
            Math.Clamp(configuration.GetValue(keys.MinimumResourcesPercent, 90), 0, 100),
            Math.Max(1, configuration.GetValue(keys.TimedMinMinutes, 30)),
            Math.Max(1, configuration.GetValue(keys.TimedMaxMinutes, 120)),
            configuration.GetValue(keys.CheckWood, true),
            configuration.GetValue(keys.CheckClay, true),
            configuration.GetValue(keys.CheckIron, true),
            configuration.GetValue(keys.CheckCrop, false))
        {
            MinimumTroopsEnabled = configuration.GetValue(keys.MinimumTroopsEnabled, false),
            MaximumMinimumTroops = Math.Clamp(configuration.GetValue(keys.MaximumMinimumTroops, 100), 1, 10000),
            AutomaticResourceSelection = configuration.GetValue(keys.AutomaticResourceSelection, false),
        };

    private static string NormalizeRunMode(string? value)
        => string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase) ? "resource_percent" : "timed";

    private static string NormalizeConfiguredRunMode(string? value)
        => string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase)
            ? "resource_percent"
            : "timed";

    private static int NormalizeFallbackCooldown(int value)
        => value is 10 or 30 or 60 or 120 or 300 or 600 ? value : 30;

    private static TroopTrainingBuildingOptions CreateBarracks(BotOptions source)
        => new(source.TroopTrainingBarracksEnabled, source.TroopTrainingBarracksTroopType, source.TroopTrainingBarracksMaxQueueHours, source.TroopTrainingBarracksAmountMode, source.TroopTrainingBarracksKeepResourcesPercent, source.TroopTrainingBarracksRunMode, source.TroopTrainingBarracksMinimumTroops, source.TroopTrainingBarracksMinimumResourcesPercent, source.TroopTrainingBarracksTimedMinMinutes, source.TroopTrainingBarracksTimedMaxMinutes, source.TroopTrainingBarracksCheckWood, source.TroopTrainingBarracksCheckClay, source.TroopTrainingBarracksCheckIron, source.TroopTrainingBarracksCheckCrop) { MinimumTroopsEnabled = source.TroopTrainingBarracksMinimumTroopsEnabled, MaximumMinimumTroops = source.TroopTrainingBarracksMaximumMinimumTroops, AutomaticResourceSelection = source.TroopTrainingBarracksAutomaticResourceSelection };

    private static TroopTrainingBuildingOptions CreateStable(BotOptions source)
        => new(source.TroopTrainingStableEnabled, source.TroopTrainingStableTroopType, source.TroopTrainingStableMaxQueueHours, source.TroopTrainingStableAmountMode, source.TroopTrainingStableKeepResourcesPercent, source.TroopTrainingStableRunMode, source.TroopTrainingStableMinimumTroops, source.TroopTrainingStableMinimumResourcesPercent, source.TroopTrainingStableTimedMinMinutes, source.TroopTrainingStableTimedMaxMinutes, source.TroopTrainingStableCheckWood, source.TroopTrainingStableCheckClay, source.TroopTrainingStableCheckIron, source.TroopTrainingStableCheckCrop) { MinimumTroopsEnabled = source.TroopTrainingStableMinimumTroopsEnabled, MaximumMinimumTroops = source.TroopTrainingStableMaximumMinimumTroops, AutomaticResourceSelection = source.TroopTrainingStableAutomaticResourceSelection };

    private static TroopTrainingBuildingOptions CreateWorkshop(BotOptions source)
        => new(source.TroopTrainingWorkshopEnabled, source.TroopTrainingWorkshopTroopType, source.TroopTrainingWorkshopMaxQueueHours, source.TroopTrainingWorkshopAmountMode, source.TroopTrainingWorkshopKeepResourcesPercent, source.TroopTrainingWorkshopRunMode, source.TroopTrainingWorkshopMinimumTroops, source.TroopTrainingWorkshopMinimumResourcesPercent, source.TroopTrainingWorkshopTimedMinMinutes, source.TroopTrainingWorkshopTimedMaxMinutes, source.TroopTrainingWorkshopCheckWood, source.TroopTrainingWorkshopCheckClay, source.TroopTrainingWorkshopCheckIron, source.TroopTrainingWorkshopCheckCrop) { MinimumTroopsEnabled = source.TroopTrainingWorkshopMinimumTroopsEnabled, MaximumMinimumTroops = source.TroopTrainingWorkshopMaximumMinimumTroops, AutomaticResourceSelection = source.TroopTrainingWorkshopAutomaticResourceSelection };

    private sealed record BuildingKeys(string Enabled, string TroopType, string MaxQueueHours, string AmountMode, string KeepResourcesPercent, string RunMode, string MinimumTroopsEnabled, string MinimumTroops, string MaximumMinimumTroops, string MinimumResourcesPercent, string TimedMinMinutes, string TimedMaxMinutes, string CheckWood, string CheckClay, string CheckIron, string CheckCrop, string AutomaticResourceSelection);

    private static readonly BuildingKeys BarracksKeys = new(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, BotOptionPayloadKeys.TroopTrainingBarracksTroopType, BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours, BotOptionPayloadKeys.TroopTrainingBarracksAmountMode, BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksRunMode, BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroopsEnabled, BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, BotOptionPayloadKeys.TroopTrainingBarracksMaximumMinimumTroops, BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop, BotOptionPayloadKeys.TroopTrainingBarracksAutomaticResourceSelection);
    private static readonly BuildingKeys StableKeys = new(BotOptionPayloadKeys.TroopTrainingStableEnabled, BotOptionPayloadKeys.TroopTrainingStableTroopType, BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours, BotOptionPayloadKeys.TroopTrainingStableAmountMode, BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableRunMode, BotOptionPayloadKeys.TroopTrainingStableMinimumTroopsEnabled, BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, BotOptionPayloadKeys.TroopTrainingStableMaximumMinimumTroops, BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingStableCheckWood, BotOptionPayloadKeys.TroopTrainingStableCheckClay, BotOptionPayloadKeys.TroopTrainingStableCheckIron, BotOptionPayloadKeys.TroopTrainingStableCheckCrop, BotOptionPayloadKeys.TroopTrainingStableAutomaticResourceSelection);
    private static readonly BuildingKeys WorkshopKeys = new(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, BotOptionPayloadKeys.TroopTrainingWorkshopTroopType, BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours, BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode, BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopRunMode, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroopsEnabled, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, BotOptionPayloadKeys.TroopTrainingWorkshopMaximumMinimumTroops, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop, BotOptionPayloadKeys.TroopTrainingWorkshopAutomaticResourceSelection);
}
