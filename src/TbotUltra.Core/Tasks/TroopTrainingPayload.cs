using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;

namespace TbotUltra.Core.Tasks;

public sealed record TroopTrainingBuildingPayload(
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

public sealed record TroopTrainingPayload(
    TroopTrainingBuildingPayload Barracks,
    TroopTrainingBuildingPayload Stable,
    TroopTrainingBuildingPayload Workshop,
    int FallbackCooldownSeconds)
{
    public static bool TryFromDictionary(IReadOnlyDictionary<string, string> payload, out TroopTrainingPayload? result)
    {
        result = null;
        if (!TryReadBuilding(payload, TroopTrainingBuildingType.Barracks, out var barracks)
            || !TryReadBuilding(payload, TroopTrainingBuildingType.Stable, out var stable)
            || !TryReadBuilding(payload, TroopTrainingBuildingType.Workshop, out var workshop)
            || !TryReadInt(payload, BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, 300, 0, int.MaxValue, out var fallbackCooldownSeconds))
        {
            return false;
        }

        result = new TroopTrainingPayload(barracks, stable, workshop, fallbackCooldownSeconds);
        return true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        WriteBuilding(result, TroopTrainingBuildingType.Barracks, Barracks);
        WriteBuilding(result, TroopTrainingBuildingType.Stable, Stable);
        WriteBuilding(result, TroopTrainingBuildingType.Workshop, Workshop);
        result[BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds] = Math.Max(0, FallbackCooldownSeconds).ToString();
        return result;
    }

    private static bool TryReadBuilding(
        IReadOnlyDictionary<string, string> payload,
        TroopTrainingBuildingType buildingType,
        out TroopTrainingBuildingPayload result)
    {
        var keys = ResolveKeys(buildingType);
        result = default!;
        if (!TryReadBool(payload, keys.Enabled, false, out var enabled)
            || !TryReadInt(payload, keys.KeepResourcesPercent, 0, 0, 100, out var keepResourcesPercent)
            || !TryReadBool(payload, keys.MinimumTroopsEnabled, false, out var minimumTroopsEnabled)
            || !TryReadInt(payload, keys.MinimumTroops, 20, 1, 10000, out var minimumTroops)
            || !TryReadInt(payload, keys.MaximumMinimumTroops, 100, 1, 10000, out var maximumMinimumTroops)
            || !TryReadInt(payload, keys.MinimumResourcesPercent, 0, 0, 100, out var minimumResourcesPercent)
            || !TryReadInt(payload, keys.TimedMinMinutes, 30, 1, int.MaxValue, out var timedMinMinutes)
            || !TryReadInt(payload, keys.TimedMaxMinutes, 120, 1, int.MaxValue, out var timedMaxMinutes)
            || !TryReadBool(payload, keys.CheckWood, true, out var checkWood)
            || !TryReadBool(payload, keys.CheckClay, true, out var checkClay)
            || !TryReadBool(payload, keys.CheckIron, true, out var checkIron)
            || !TryReadBool(payload, keys.CheckCrop, true, out var checkCrop)
            || !TryReadBool(payload, keys.AutomaticResourceSelection, false, out var automaticResourceSelection))
        {
            return false;
        }

        result = new TroopTrainingBuildingPayload(
            enabled,
            ReadTrimmed(payload, keys.TroopType) ?? string.Empty,
            ReadTrimmed(payload, keys.MaxQueueHours) ?? "no_limit",
            ReadTrimmed(payload, keys.AmountMode) ?? "fixed",
            keepResourcesPercent,
            NormalizeRunMode(ReadTrimmed(payload, keys.RunMode)),
            minimumTroops,
            minimumResourcesPercent,
            timedMinMinutes,
            timedMaxMinutes,
            checkWood,
            checkClay,
            checkIron,
            checkCrop)
        {
            MinimumTroopsEnabled = minimumTroopsEnabled,
            MaximumMinimumTroops = maximumMinimumTroops,
            AutomaticResourceSelection = automaticResourceSelection,
        };
        return true;
    }

    private static void WriteBuilding(Dictionary<string, string> result, TroopTrainingBuildingType buildingType, TroopTrainingBuildingPayload value)
    {
        var keys = ResolveKeys(buildingType);
        result[keys.Enabled] = value.Enabled ? "true" : "false";
        result[keys.TroopType] = value.TroopType.Trim();
        result[keys.MaxQueueHours] = string.IsNullOrWhiteSpace(value.MaxQueueHours) ? "no_limit" : value.MaxQueueHours.Trim();
        result[keys.AmountMode] = string.IsNullOrWhiteSpace(value.AmountMode) ? "fixed" : value.AmountMode.Trim();
        result[keys.KeepResourcesPercent] = Math.Clamp(value.KeepResourcesPercent, 0, 100).ToString();
        result[keys.RunMode] = NormalizeRunMode(value.RunMode);
        result[keys.MinimumTroopsEnabled] = value.MinimumTroopsEnabled ? "true" : "false";
        result[keys.MinimumTroops] = Math.Clamp(value.MinimumTroops, 1, 10000).ToString();
        result[keys.MaximumMinimumTroops] = Math.Clamp(value.MaximumMinimumTroops, 1, 10000).ToString();
        result[keys.MinimumResourcesPercent] = Math.Clamp(value.MinimumResourcesPercent, 0, 100).ToString();
        result[keys.TimedMinMinutes] = Math.Max(1, value.TimedMinMinutes).ToString();
        result[keys.TimedMaxMinutes] = Math.Max(1, value.TimedMaxMinutes).ToString();
        result[keys.CheckWood] = value.CheckWood ? "true" : "false";
        result[keys.CheckClay] = value.CheckClay ? "true" : "false";
        result[keys.CheckIron] = value.CheckIron ? "true" : "false";
        result[keys.CheckCrop] = value.CheckCrop ? "true" : "false";
        result[keys.AutomaticResourceSelection] = value.AutomaticResourceSelection ? "true" : "false";
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, string> payload, string key, bool defaultValue, out bool value)
    {
        value = defaultValue;
        return !payload.TryGetValue(key, out var raw) || bool.TryParse(raw, out value);
    }

    private static bool TryReadInt(IReadOnlyDictionary<string, string> payload, string key, int defaultValue, int min, int max, out int value)
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

        value = Math.Clamp(parsed, min, max);
        return true;
    }

    private static string? ReadTrimmed(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static string NormalizeRunMode(string? value)
        => string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase)
            ? "resource_percent"
            : "timed";

    private static BuildingKeys ResolveKeys(TroopTrainingBuildingType buildingType)
    {
        return buildingType switch
        {
            TroopTrainingBuildingType.Barracks => new(
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
                BotOptionPayloadKeys.TroopTrainingBarracksAutomaticResourceSelection),
            TroopTrainingBuildingType.Stable => new(
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
                BotOptionPayloadKeys.TroopTrainingStableAutomaticResourceSelection),
            _ => new(
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
                BotOptionPayloadKeys.TroopTrainingWorkshopAutomaticResourceSelection),
        };
    }

    private sealed record BuildingKeys(
        string Enabled,
        string TroopType,
        string MaxQueueHours,
        string AmountMode,
        string KeepResourcesPercent,
        string RunMode,
        string MinimumTroopsEnabled,
        string MinimumTroops,
        string MaximumMinimumTroops,
        string MinimumResourcesPercent,
        string TimedMinMinutes,
        string TimedMaxMinutes,
        string CheckWood,
        string CheckClay,
        string CheckIron,
        string CheckCrop,
        string AutomaticResourceSelection);
}
