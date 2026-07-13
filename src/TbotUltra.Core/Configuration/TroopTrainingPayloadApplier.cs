namespace TbotUltra.Core.Configuration;

internal sealed record TroopTrainingBuildingPayloadValues(
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
    bool CheckCrop);

internal sealed record TroopTrainingPayloadValues(
    TroopTrainingBuildingPayloadValues Barracks,
    TroopTrainingBuildingPayloadValues Stable,
    TroopTrainingBuildingPayloadValues Workshop,
    int FallbackCooldownSeconds,
    bool BreweryAutoCelebrationEnabled);

internal static class TroopTrainingPayloadApplier
{
    internal static TroopTrainingPayloadValues Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
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

        return new TroopTrainingPayloadValues(barracks, stable, workshop, fallbackCooldown, breweryCelebration);
    }

    private static TroopTrainingBuildingPayloadValues ApplyBuilding(
        TroopTrainingBuildingPayloadValues source,
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
            else if (TryReadInt(key, value, keys.MinimumTroops, out var minimumTroops))
                result = result with { MinimumTroops = Math.Max(1, minimumTroops) };
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
        }

        return result;
    }

    private static string NormalizeRunMode(string? value)
        => string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase) ? "resource_percent" : "timed";

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

    private static TroopTrainingBuildingPayloadValues CreateBarracks(BotOptions source)
        => new(source.TroopTrainingBarracksEnabled, source.TroopTrainingBarracksTroopType, source.TroopTrainingBarracksMaxQueueHours, source.TroopTrainingBarracksAmountMode, source.TroopTrainingBarracksKeepResourcesPercent, source.TroopTrainingBarracksRunMode, source.TroopTrainingBarracksMinimumTroops, source.TroopTrainingBarracksMinimumResourcesPercent, source.TroopTrainingBarracksTimedMinMinutes, source.TroopTrainingBarracksTimedMaxMinutes, source.TroopTrainingBarracksCheckWood, source.TroopTrainingBarracksCheckClay, source.TroopTrainingBarracksCheckIron, source.TroopTrainingBarracksCheckCrop);

    private static TroopTrainingBuildingPayloadValues CreateStable(BotOptions source)
        => new(source.TroopTrainingStableEnabled, source.TroopTrainingStableTroopType, source.TroopTrainingStableMaxQueueHours, source.TroopTrainingStableAmountMode, source.TroopTrainingStableKeepResourcesPercent, source.TroopTrainingStableRunMode, source.TroopTrainingStableMinimumTroops, source.TroopTrainingStableMinimumResourcesPercent, source.TroopTrainingStableTimedMinMinutes, source.TroopTrainingStableTimedMaxMinutes, source.TroopTrainingStableCheckWood, source.TroopTrainingStableCheckClay, source.TroopTrainingStableCheckIron, source.TroopTrainingStableCheckCrop);

    private static TroopTrainingBuildingPayloadValues CreateWorkshop(BotOptions source)
        => new(source.TroopTrainingWorkshopEnabled, source.TroopTrainingWorkshopTroopType, source.TroopTrainingWorkshopMaxQueueHours, source.TroopTrainingWorkshopAmountMode, source.TroopTrainingWorkshopKeepResourcesPercent, source.TroopTrainingWorkshopRunMode, source.TroopTrainingWorkshopMinimumTroops, source.TroopTrainingWorkshopMinimumResourcesPercent, source.TroopTrainingWorkshopTimedMinMinutes, source.TroopTrainingWorkshopTimedMaxMinutes, source.TroopTrainingWorkshopCheckWood, source.TroopTrainingWorkshopCheckClay, source.TroopTrainingWorkshopCheckIron, source.TroopTrainingWorkshopCheckCrop);

    private sealed record BuildingKeys(string Enabled, string TroopType, string MaxQueueHours, string AmountMode, string KeepResourcesPercent, string RunMode, string MinimumTroops, string MinimumResourcesPercent, string TimedMinMinutes, string TimedMaxMinutes, string CheckWood, string CheckClay, string CheckIron, string CheckCrop);

    private static readonly BuildingKeys BarracksKeys = new(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, BotOptionPayloadKeys.TroopTrainingBarracksTroopType, BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours, BotOptionPayloadKeys.TroopTrainingBarracksAmountMode, BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksRunMode, BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop);
    private static readonly BuildingKeys StableKeys = new(BotOptionPayloadKeys.TroopTrainingStableEnabled, BotOptionPayloadKeys.TroopTrainingStableTroopType, BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours, BotOptionPayloadKeys.TroopTrainingStableAmountMode, BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableRunMode, BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingStableCheckWood, BotOptionPayloadKeys.TroopTrainingStableCheckClay, BotOptionPayloadKeys.TroopTrainingStableCheckIron, BotOptionPayloadKeys.TroopTrainingStableCheckCrop);
    private static readonly BuildingKeys WorkshopKeys = new(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, BotOptionPayloadKeys.TroopTrainingWorkshopTroopType, BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours, BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode, BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopRunMode, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes, BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop);
}
