using Microsoft.Extensions.Configuration;
using static TbotUltra.Core.Configuration.PayloadValueReader;

namespace TbotUltra.Core.Configuration;

internal sealed record FarmingOptions(
    List<string> ListNames,
    List<string> ListIds,
    int DispatchDelayMinMinutes,
    int DispatchDelayMaxMinutes,
    string SendMode,
    string TownHallCelebrationMode,
    bool DeactivateLosses,
    bool DeactivateOasisLosses,
    bool MoveLosses,
    string LossDestinationListId,
    string LossDestinationListName,
    string LossDestinationBaseName,
    bool DeactivateRedLosses,
    bool DeactivateYellowLosses,
    bool DeactivateRedOasisLosses,
    bool DeactivateYellowOasisLosses,
    bool MoveRedLosses,
    bool MoveYellowLosses,
    string RedLossDestinationListId,
    string RedLossDestinationListName,
    string RedLossDestinationBaseName,
    string YellowLossDestinationListId,
    string YellowLossDestinationListName,
    string YellowLossDestinationBaseName,
    int NextListIndex)
{
    public bool OnlyCreateReportsWithLosses { get; init; }
    public bool ShowLastSentTimer { get; init; }
    public bool LastSentLimitEnabled { get; init; }
    public int LastSentLimitHours { get; init; }
    public int TownHallCelebrationCount { get; init; }
    public double TownHallRestartDelayMinMinutes { get; init; }
    public double TownHallRestartDelayMaxMinutes { get; init; }
    public bool TownHallRestartDelayEnabled { get; init; }
    public double BreweryRestartDelayMinMinutes { get; init; }
    public double BreweryRestartDelayMaxMinutes { get; init; }
    public bool BreweryRestartDelayEnabled { get; init; }
}

internal static class FarmingOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.ContinuousFarmListNames,
        BotOptionPayloadKeys.ContinuousFarmListIds,
        BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes,
        BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes,
        BotOptionPayloadKeys.ContinuousFarmSendMode,
        BotOptionPayloadKeys.FarmListOnlyCreateReportsWithLosses,
        BotOptionPayloadKeys.AddFarmsExcludeOwnAlliance,
        BotOptionPayloadKeys.AddFarmsExcludedPlayers,
        BotOptionPayloadKeys.AddFarmsExcludedAlliances,
        BotOptionPayloadKeys.ShowFarmListLastSentTimer,
        BotOptionPayloadKeys.FarmListLastSentLimitEnabled,
        BotOptionPayloadKeys.FarmListLastSentLimitHours,
        BotOptionPayloadKeys.TownHallCelebrationMode,
        BotOptionPayloadKeys.TownHallCelebrationCount,
        BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes,
        BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes,
        BotOptionPayloadKeys.ContinuousFarmDeactivateLosses,
        BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses,
        BotOptionPayloadKeys.ContinuousFarmMoveLosses,
        BotOptionPayloadKeys.ContinuousFarmLossDestinationListId,
        BotOptionPayloadKeys.ContinuousFarmLossDestinationListName,
        BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName,
        BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses,
        BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses,
        BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses,
        BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses,
        BotOptionPayloadKeys.ContinuousFarmMoveRedLosses,
        BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses,
        BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId,
        BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName,
        BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName,
        BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId,
        BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName,
        BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName,
        "addFarmsTroopCount",
    ];

    internal static FarmingOptions FromConfiguration(IConfiguration configuration)
    {
        var deactivateLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateLosses, true);
        var deactivateOasisLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses, false);
        var moveLosses = deactivateLosses
            && configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmMoveLosses, false);
        var deactivateRed = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses, deactivateLosses);
        var deactivateYellow = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses, deactivateLosses);
        var deactivateRedOasis = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses, deactivateOasisLosses);
        var deactivateYellowOasis = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses, deactivateOasisLosses);
        var legacyListId = configuration[BotOptionPayloadKeys.ContinuousFarmLossDestinationListId] ?? string.Empty;
        var legacyListName = configuration[BotOptionPayloadKeys.ContinuousFarmLossDestinationListName] ?? string.Empty;
        var legacyBaseName = configuration[BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName] ?? string.Empty;

        return new FarmingOptions(
            configuration.GetSection(BotOptionPayloadKeys.ContinuousFarmListNames).Get<List<string>>() ?? [],
            configuration.GetSection(BotOptionPayloadKeys.ContinuousFarmListIds).Get<List<string>>() ?? [],
            FarmingDefaults.NormalizeDispatchDelayMinMinutes(configuration.GetValue(
                BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes,
                FarmingDefaults.DefaultDispatchDelayMinMinutes)),
            FarmingDefaults.NormalizeDispatchDelayMaxMinutes(configuration.GetValue(
                BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes,
                FarmingDefaults.DefaultDispatchDelayMaxMinutes)),
            FarmingDefaults.NormalizeSendMode(configuration[BotOptionPayloadKeys.ContinuousFarmSendMode]),
            TownHallCelebrationDefaults.NormalizeMode(configuration[BotOptionPayloadKeys.TownHallCelebrationMode]),
            deactivateLosses,
            deactivateOasisLosses,
            moveLosses,
            legacyListId,
            legacyListName,
            legacyBaseName,
            deactivateRed,
            deactivateYellow,
            deactivateRedOasis,
            deactivateYellowOasis,
            deactivateRed && GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmMoveRedLosses, moveLosses),
            deactivateYellow && GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses, moveLosses),
            configuration[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId] ?? legacyListId,
            configuration[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName] ?? legacyListName,
            configuration[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName] ?? legacyBaseName,
            configuration[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId] ?? legacyListId,
            configuration[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName] ?? legacyListName,
            configuration[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName] ?? legacyBaseName,
            0)
        {
            OnlyCreateReportsWithLosses = configuration.GetValue(
                BotOptionPayloadKeys.FarmListOnlyCreateReportsWithLosses,
                FarmingDefaults.OnlyCreateReportsWithLosses),
            ShowLastSentTimer = configuration.GetValue(
                BotOptionPayloadKeys.ShowFarmListLastSentTimer,
                FarmingDefaults.ShowLastSentTimer),
            LastSentLimitEnabled = configuration.GetValue(
                BotOptionPayloadKeys.FarmListLastSentLimitEnabled,
                FarmingDefaults.LastSentLimitEnabled),
            LastSentLimitHours = FarmingDefaults.NormalizeLastSentLimitHours(configuration.GetValue(
                BotOptionPayloadKeys.FarmListLastSentLimitHours,
                FarmingDefaults.DefaultLastSentLimitHours)),
            TownHallCelebrationCount = TownHallCelebrationDefaults.NormalizeCount(configuration.GetValue(
                BotOptionPayloadKeys.TownHallCelebrationCount,
                TownHallCelebrationDefaults.DefaultCount)),
            TownHallRestartDelayMinMinutes = configuration.GetValue(
                BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes,
                TownHallCelebrationDefaults.DefaultRestartDelayMinMinutes),
            TownHallRestartDelayMaxMinutes = configuration.GetValue(
                BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes,
                TownHallCelebrationDefaults.DefaultRestartDelayMaxMinutes),
            TownHallRestartDelayEnabled = configuration.GetValue(
                BotOptionPayloadKeys.TownHallCelebrationRestartDelayEnabled,
                TownHallCelebrationDefaults.DefaultRestartDelayEnabled),
            BreweryRestartDelayMinMinutes = configuration.GetValue(
                BotOptionPayloadKeys.BreweryCelebrationRestartDelayMinMinutes,
                BreweryCelebrationDefaults.DefaultRestartDelayMinMinutes),
            BreweryRestartDelayMaxMinutes = configuration.GetValue(
                BotOptionPayloadKeys.BreweryCelebrationRestartDelayMaxMinutes,
                BreweryCelebrationDefaults.DefaultRestartDelayMaxMinutes),
            BreweryRestartDelayEnabled = configuration.GetValue(
                BotOptionPayloadKeys.BreweryCelebrationRestartDelayEnabled,
                BreweryCelebrationDefaults.DefaultRestartDelayEnabled),
        };
    }

    internal static FarmingOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new FarmingOptions(
            source.ContinuousFarmListNames,
            source.ContinuousFarmListIds,
            source.ContinuousFarmDispatchDelayMinMinutes,
            source.ContinuousFarmDispatchDelayMaxMinutes,
            source.ContinuousFarmSendMode,
            source.TownHallCelebrationMode,
            source.ContinuousFarmDeactivateLosses,
            source.ContinuousFarmDeactivateOasisLosses,
            source.ContinuousFarmMoveLosses,
            source.ContinuousFarmLossDestinationListId,
            source.ContinuousFarmLossDestinationListName,
            source.ContinuousFarmLossDestinationBaseName,
            source.ContinuousFarmDeactivateRedLosses,
            source.ContinuousFarmDeactivateYellowLosses,
            source.ContinuousFarmDeactivateRedOasisLosses,
            source.ContinuousFarmDeactivateYellowOasisLosses,
            source.ContinuousFarmMoveRedLosses,
            source.ContinuousFarmMoveYellowLosses,
            source.ContinuousFarmRedLossDestinationListId,
            source.ContinuousFarmRedLossDestinationListName,
            source.ContinuousFarmRedLossDestinationBaseName,
            source.ContinuousFarmYellowLossDestinationListId,
            source.ContinuousFarmYellowLossDestinationListName,
            source.ContinuousFarmYellowLossDestinationBaseName,
            source.ContinuousFarmNextListIndex)
        {
            OnlyCreateReportsWithLosses = source.FarmListOnlyCreateReportsWithLosses,
            ShowLastSentTimer = source.ShowFarmListLastSentTimer,
            LastSentLimitEnabled = source.FarmListLastSentLimitEnabled,
            LastSentLimitHours = source.FarmListLastSentLimitHours,
            TownHallCelebrationCount = source.TownHallCelebrationCount,
            TownHallRestartDelayMinMinutes = source.TownHallCelebrationRestartDelayMinMinutes,
            TownHallRestartDelayMaxMinutes = source.TownHallCelebrationRestartDelayMaxMinutes,
            TownHallRestartDelayEnabled = source.TownHallCelebrationRestartDelayEnabled,
            BreweryRestartDelayMinMinutes = source.BreweryCelebrationRestartDelayMinMinutes,
            BreweryRestartDelayMaxMinutes = source.BreweryCelebrationRestartDelayMaxMinutes,
            BreweryRestartDelayEnabled = source.BreweryCelebrationRestartDelayEnabled,
        };

        if (payload is null)
            return NormalizePayloadAliases(result);

        ApplyLegacyFallbacks(payload, ref result);

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
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses, out var deactivateRed))
                result = result with { DeactivateRedLosses = deactivateRed };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses, out var deactivateYellow))
                result = result with { DeactivateYellowLosses = deactivateYellow };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses, out var deactivateRedOasis))
                result = result with { DeactivateRedOasisLosses = deactivateRedOasis };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses, out var deactivateYellowOasis))
                result = result with { DeactivateYellowOasisLosses = deactivateYellowOasis };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmMoveRedLosses, out var moveRed))
                result = result with { MoveRedLosses = moveRed };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses, out var moveYellow))
                result = result with { MoveYellowLosses = moveYellow };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId, StringComparison.OrdinalIgnoreCase))
                result = result with { RedLossDestinationListId = value };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName, StringComparison.OrdinalIgnoreCase))
                result = result with { RedLossDestinationListName = value };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName, StringComparison.OrdinalIgnoreCase))
                result = result with { RedLossDestinationBaseName = value };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId, StringComparison.OrdinalIgnoreCase))
                result = result with { YellowLossDestinationListId = value };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName, StringComparison.OrdinalIgnoreCase))
                result = result with { YellowLossDestinationListName = value };
            else if (key.Equals(BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName, StringComparison.OrdinalIgnoreCase))
                result = result with { YellowLossDestinationBaseName = value };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.ContinuousFarmNextListIndex, out var nextIndex))
                result = result with { NextListIndex = Math.Max(0, nextIndex) };
        }

        return NormalizePayloadAliases(result);
    }

    internal static BotOptions ApplyTo(this FarmingOptions values, BotOptions source)
        => source with
        {
            ContinuousFarmListNames = values.ListNames,
            ContinuousFarmListIds = values.ListIds,
            ContinuousFarmDispatchDelayMinMinutes = values.DispatchDelayMinMinutes,
            ContinuousFarmDispatchDelayMaxMinutes = values.DispatchDelayMaxMinutes,
            ContinuousFarmSendMode = values.SendMode,
            TownHallCelebrationMode = values.TownHallCelebrationMode,
            ContinuousFarmDeactivateLosses = values.DeactivateLosses,
            ContinuousFarmDeactivateOasisLosses = values.DeactivateOasisLosses,
            ContinuousFarmMoveLosses = values.MoveLosses,
            ContinuousFarmLossDestinationListId = values.LossDestinationListId,
            ContinuousFarmLossDestinationListName = values.LossDestinationListName,
            ContinuousFarmLossDestinationBaseName = values.LossDestinationBaseName,
            ContinuousFarmDeactivateRedLosses = values.DeactivateRedLosses,
            ContinuousFarmDeactivateYellowLosses = values.DeactivateYellowLosses,
            ContinuousFarmDeactivateRedOasisLosses = values.DeactivateRedOasisLosses,
            ContinuousFarmDeactivateYellowOasisLosses = values.DeactivateYellowOasisLosses,
            ContinuousFarmMoveRedLosses = values.DeactivateRedLosses && values.MoveRedLosses,
            ContinuousFarmMoveYellowLosses = values.DeactivateYellowLosses && values.MoveYellowLosses,
            ContinuousFarmRedLossDestinationListId = values.RedLossDestinationListId,
            ContinuousFarmRedLossDestinationListName = values.RedLossDestinationListName,
            ContinuousFarmRedLossDestinationBaseName = values.RedLossDestinationBaseName,
            ContinuousFarmYellowLossDestinationListId = values.YellowLossDestinationListId,
            ContinuousFarmYellowLossDestinationListName = values.YellowLossDestinationListName,
            ContinuousFarmYellowLossDestinationBaseName = values.YellowLossDestinationBaseName,
            ContinuousFarmNextListIndex = values.NextListIndex,
            FarmListOnlyCreateReportsWithLosses = values.OnlyCreateReportsWithLosses,
            ShowFarmListLastSentTimer = values.ShowLastSentTimer,
            FarmListLastSentLimitEnabled = values.LastSentLimitEnabled,
            FarmListLastSentLimitHours = values.LastSentLimitHours,
            TownHallCelebrationCount = values.TownHallCelebrationCount,
            TownHallCelebrationRestartDelayMinMinutes = values.TownHallRestartDelayMinMinutes,
            TownHallCelebrationRestartDelayMaxMinutes = values.TownHallRestartDelayMaxMinutes,
            TownHallCelebrationRestartDelayEnabled = values.TownHallRestartDelayEnabled,
            BreweryCelebrationRestartDelayMinMinutes = values.BreweryRestartDelayMinMinutes,
            BreweryCelebrationRestartDelayMaxMinutes = values.BreweryRestartDelayMaxMinutes,
            BreweryCelebrationRestartDelayEnabled = values.BreweryRestartDelayEnabled,
        };

    private static FarmingOptions NormalizePayloadAliases(FarmingOptions values)
        => values with
        {
            MoveRedLosses = values.DeactivateRedLosses && values.MoveRedLosses,
            MoveYellowLosses = values.DeactivateYellowLosses && values.MoveYellowLosses,
            DeactivateLosses = values.DeactivateRedLosses || values.DeactivateYellowLosses,
            DeactivateOasisLosses = values.DeactivateRedOasisLosses || values.DeactivateYellowOasisLosses,
            MoveLosses = (values.DeactivateRedLosses && values.MoveRedLosses)
                || (values.DeactivateYellowLosses && values.MoveYellowLosses),
            LossDestinationListId = values.YellowLossDestinationListId,
            LossDestinationListName = values.YellowLossDestinationListName,
            LossDestinationBaseName = values.YellowLossDestinationBaseName,
        };

    private static void ApplyLegacyFallbacks(IReadOnlyDictionary<string, string> payload, ref FarmingOptions result)
    {
        if (TryGetBool(payload, BotOptionPayloadKeys.ContinuousFarmDeactivateLosses, out var deactivateLosses))
        {
            if (!ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses))
                result = result with { DeactivateRedLosses = deactivateLosses };
            if (!ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses))
                result = result with { DeactivateYellowLosses = deactivateLosses };
        }

        if (TryGetBool(payload, BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses, out var deactivateOasisLosses))
        {
            if (!ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses))
                result = result with { DeactivateRedOasisLosses = deactivateOasisLosses };
            if (!ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses))
                result = result with { DeactivateYellowOasisLosses = deactivateOasisLosses };
        }

        if (TryGetBool(payload, BotOptionPayloadKeys.ContinuousFarmMoveLosses, out var moveLosses))
        {
            if (!ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmMoveRedLosses))
                result = result with { MoveRedLosses = moveLosses };
            if (!ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses))
                result = result with { MoveYellowLosses = moveLosses };
        }

        var legacyListId = ReadLegacyString(payload, BotOptionPayloadKeys.ContinuousFarmLossDestinationListId);
        var legacyListName = ReadLegacyString(payload, BotOptionPayloadKeys.ContinuousFarmLossDestinationListName);
        var legacyBaseName = ReadLegacyString(payload, BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName);
        result = result with
        {
            RedLossDestinationListId = ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId) || legacyListId is null ? result.RedLossDestinationListId : legacyListId,
            YellowLossDestinationListId = ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId) || legacyListId is null ? result.YellowLossDestinationListId : legacyListId,
            RedLossDestinationListName = ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName) || legacyListName is null ? result.RedLossDestinationListName : legacyListName,
            YellowLossDestinationListName = ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName) || legacyListName is null ? result.YellowLossDestinationListName : legacyListName,
            RedLossDestinationBaseName = ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName) || legacyBaseName is null ? result.RedLossDestinationBaseName : legacyBaseName,
            YellowLossDestinationBaseName = ContainsKey(payload, BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName) || legacyBaseName is null ? result.YellowLossDestinationBaseName : legacyBaseName,
        };
    }

    private static string? ReadLegacyString(IReadOnlyDictionary<string, string> payload, string key)
    {
        var value = payload.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool ContainsKey(IReadOnlyDictionary<string, string> payload, string key)
        => payload.Keys.Any(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetBool(IReadOnlyDictionary<string, string> payload, string key, out bool parsed)
    {
        var value = payload.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        return bool.TryParse(value, out parsed);
    }

    private static List<string> ParseList(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool GetValueOrDefault(IConfiguration configuration, string key, bool defaultValue)
        => configuration[key] is null
            ? defaultValue
            : configuration.GetValue(key, defaultValue);
}
