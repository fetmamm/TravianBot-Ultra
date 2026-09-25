using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

internal sealed record KeepAliveOptions(bool Enabled, int MinMinutes, int MaxMinutes);

internal sealed record VillageStatusSweepOptions(
    bool Enabled,
    bool Dorf1Enabled,
    bool Dorf2Enabled,
    bool SmithyEnabled,
    bool BarracksEnabled,
    bool StableEnabled,
    bool WorkshopEnabled,
    bool TownHallEnabled,
    bool BreweryEnabled,
    int RoundMinMinutes,
    int RoundMaxMinutes,
    double VillageMinSeconds,
    double VillageMaxSeconds);

internal sealed record IdleActivityOptions(
    bool BreakEnabled,
    double BreakIntervalMinMinutes,
    double BreakIntervalMaxMinutes,
    double BreakDurationMinMinutes,
    double BreakDurationMaxMinutes,
    bool BrowseEnabled,
    double BrowseIntervalMinMinutes,
    double BrowseIntervalMaxMinutes,
    bool BrowseMap,
    bool BrowseStatistics,
    bool BrowseStatisticsHero,
    bool BrowseStatisticsTop10,
    bool BrowseStatisticsDefenders,
    bool BrowseStatisticsAttackers,
    bool BrowseReports,
    bool BrowseMessages);

internal sealed record ActionPacingOptions(
    bool HumanLikeEnabled,
    string HumanLikeSpeed,
    bool Enabled,
    double TaskMinSeconds,
    double TaskMaxSeconds,
    double PageLoadMinSeconds,
    double PageLoadMaxSeconds,
    double ClickMinSeconds,
    double ClickMaxSeconds,
    double LoopMinSeconds,
    double LoopMaxSeconds,
    double FarmListStepMinSeconds,
    double FarmListStepMaxSeconds,
    int ShortVillageDeferSeconds,
    int VillageRoundSleepExtensionMinutes)
{
    public required KeepAliveOptions KeepAlive { get; init; }
    public required VillageStatusSweepOptions VillageStatusSweep { get; init; }
    public required IdleActivityOptions IdleActivity { get; init; }
}

/// <summary>
/// Applies action-pacing payload keys without owning unrelated bot options.
/// </summary>
internal static class ActionPacingOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.SessionPacingEnabled,
        BotOptionPayloadKeys.SessionPacingRunMinMinutes,
        BotOptionPayloadKeys.SessionPacingRunMaxMinutes,
        BotOptionPayloadKeys.SessionPacingSleepMinMinutes,
        BotOptionPayloadKeys.SessionPacingSleepMaxMinutes,
        BotOptionPayloadKeys.SessionPacingAllowedHours,
        BotOptionPayloadKeys.SessionPacingDailyMaxHours,
        BotOptionPayloadKeys.SessionPacingRuntimeDate,
        BotOptionPayloadKeys.SessionPacingRuntimeSeconds,
        BotOptionPayloadKeys.SessionPacingDailyHistory,
        BotOptionPayloadKeys.SmartSleepEnabled,
        BotOptionPayloadKeys.SmartSleepMinimumOpportunityMinutes,
        BotOptionPayloadKeys.SmartSleepWakeBeforeMinutes,
        BotOptionPayloadKeys.SmartSleepWakeAfterMinutes,
        BotOptionPayloadKeys.SmartSleepFallbackMinMinutes,
        BotOptionPayloadKeys.SmartSleepFallbackMaxMinutes,
        BotOptionPayloadKeys.SmartSleepDeadlineGroups,
        BotOptionPayloadKeys.SmartSleepWakeWhenConstructionQueueClears,
        BotOptionPayloadKeys.SessionActivityHistory,
        BotOptionPayloadKeys.ActionPacingEnabled,
        BotOptionPayloadKeys.ActionPacingTaskMinSeconds,
        BotOptionPayloadKeys.ActionPacingTaskMaxSeconds,
        BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds,
        BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds,
        BotOptionPayloadKeys.ActionPacingClickMinSeconds,
        BotOptionPayloadKeys.ActionPacingClickMaxSeconds,
        BotOptionPayloadKeys.ActionPacingLoopMinSeconds,
        BotOptionPayloadKeys.ActionPacingLoopMaxSeconds,
        BotOptionPayloadKeys.ShortVillageDeferSeconds,
        BotOptionPayloadKeys.VillageRoundSleepExtensionMinutes,
        BotOptionPayloadKeys.ContinuousKeepAliveEnabled,
        BotOptionPayloadKeys.ContinuousKeepAliveMinMinutes,
        BotOptionPayloadKeys.ContinuousKeepAliveMaxMinutes,
        BotOptionPayloadKeys.FarmListStepDelayMinSeconds,
        BotOptionPayloadKeys.FarmListStepDelayMaxSeconds,
        BotOptionPayloadKeys.ActionPacingIdleBreakEnabled,
        BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes,
        BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes,
        BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes,
        BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes,
        BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled,
        BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes,
        BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports,
        BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages,
        "human_like_enabled",
        "human_like_speed",
    ];

    internal static ActionPacingOptions FromConfiguration(IConfiguration configuration)
    {
        var legacy = LegacyActionPacingCompatibility.Resolve(configuration);
        var defaults = legacy.Fallbacks;
        var farmMin = ReadDelay(configuration, BotOptionPayloadKeys.FarmListStepDelayMinSeconds, defaults.FarmListMinSeconds);

        return new ActionPacingOptions(
            legacy.HumanLikeEnabled,
            legacy.HumanLikeSpeed,
            legacy.ActionPacingEnabled,
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingTaskMinSeconds, defaults.TaskMinSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, defaults.TaskMaxSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, defaults.PageLoadMinSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, defaults.PageLoadMaxSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingClickMinSeconds, defaults.ClickMinSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingClickMaxSeconds, defaults.ClickMaxSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingLoopMinSeconds, defaults.LoopMinSeconds),
            ReadDelay(configuration, BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, defaults.LoopMaxSeconds),
            farmMin,
            Math.Max(farmMin, ReadDelay(configuration, BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, defaults.FarmListMaxSeconds)),
            PacingDefaults.NormalizeShortVillageDeferSeconds(configuration.GetValue(
                BotOptionPayloadKeys.ShortVillageDeferSeconds,
                PacingDefaults.ShortVillageDeferSeconds)),
            PacingDefaults.NormalizeVillageRoundSleepExtensionMinutes(configuration.GetValue(
                BotOptionPayloadKeys.VillageRoundSleepExtensionMinutes,
                PacingDefaults.VillageRoundSleepExtensionMinutes)))
        {
            KeepAlive = new KeepAliveOptions(
                configuration.GetValue(BotOptionPayloadKeys.ContinuousKeepAliveEnabled, PacingDefaults.ContinuousKeepAliveEnabled),
                Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ContinuousKeepAliveMinMinutes, PacingDefaults.ContinuousKeepAliveMinMinutes), 1, 1440),
                Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ContinuousKeepAliveMaxMinutes, PacingDefaults.ContinuousKeepAliveMaxMinutes), 1, 1440)),
            VillageStatusSweep = new VillageStatusSweepOptions(
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepEnabled, PacingDefaults.VillageStatusSweepEnabled),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepDorf1Enabled, true),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepDorf2Enabled, false),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepSmithyEnabled, false),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepBarracksEnabled, false),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepStableEnabled, false),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepWorkshopEnabled, false),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepTownHallEnabled, false),
                configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepBreweryEnabled, false),
                Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepRoundMinMinutes, PacingDefaults.VillageStatusSweepRoundMinMinutes), 1, 1440),
                Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepRoundMaxMinutes, PacingDefaults.VillageStatusSweepRoundMaxMinutes), 1, 1440),
                ReadDelay(configuration, BotOptionPayloadKeys.VillageStatusSweepVillageMinSeconds, PacingDefaults.VillageStatusSweepVillageMinSeconds),
                ReadDelay(configuration, BotOptionPayloadKeys.VillageStatusSweepVillageMaxSeconds, PacingDefaults.VillageStatusSweepVillageMaxSeconds)),
            IdleActivity = new IdleActivityOptions(
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakEnabled, PacingDefaults.ActionPacingIdleBreakEnabled),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes, PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes, PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes, PacingDefaults.ActionPacingIdleBreakDurationMinMinutes),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes, PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled, PacingDefaults.ActionPacingIdleBrowseEnabled),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes, PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes, PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap, PacingDefaults.ActionPacingIdleBrowsePageMap),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics, PacingDefaults.ActionPacingIdleBrowsePageStatistics),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero, PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10, PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders, PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers, PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports, PacingDefaults.ActionPacingIdleBrowsePageReports),
                configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages, PacingDefaults.ActionPacingIdleBrowsePageMessages)),
        };
    }

    internal static ActionPacingOptions Apply(
        BotOptions source,
        IReadOnlyDictionary<string, string>? payload)
    {
        var result = new ActionPacingOptions(
            source.HumanLikeEnabled,
            source.HumanLikeSpeed,
            PacingDefaults.ActionPacingEnabled,
            source.ActionPacingTaskMinSeconds,
            source.ActionPacingTaskMaxSeconds,
            source.ActionPacingPageLoadMinSeconds,
            source.ActionPacingPageLoadMaxSeconds,
            source.ActionPacingClickMinSeconds,
            source.ActionPacingClickMaxSeconds,
            source.ActionPacingLoopMinSeconds,
            source.ActionPacingLoopMaxSeconds,
            source.FarmListStepDelayMinSeconds,
            source.FarmListStepDelayMaxSeconds,
            PacingDefaults.NormalizeShortVillageDeferSeconds(source.ShortVillageDeferSeconds),
            PacingDefaults.NormalizeVillageRoundSleepExtensionMinutes(source.VillageRoundSleepExtensionMinutes))
        {
            KeepAlive = new KeepAliveOptions(
                source.ContinuousKeepAliveEnabled,
                source.ContinuousKeepAliveMinMinutes,
                source.ContinuousKeepAliveMaxMinutes),
            VillageStatusSweep = new VillageStatusSweepOptions(
                source.VillageStatusSweepEnabled,
                source.VillageStatusSweepDorf1Enabled,
                source.VillageStatusSweepDorf2Enabled,
                source.VillageStatusSweepSmithyEnabled,
                source.VillageStatusSweepBarracksEnabled,
                source.VillageStatusSweepStableEnabled,
                source.VillageStatusSweepWorkshopEnabled,
                source.VillageStatusSweepTownHallEnabled,
                source.VillageStatusSweepBreweryEnabled,
                source.VillageStatusSweepRoundMinMinutes,
                source.VillageStatusSweepRoundMaxMinutes,
                source.VillageStatusSweepVillageMinSeconds,
                source.VillageStatusSweepVillageMaxSeconds),
            IdleActivity = new IdleActivityOptions(
                source.ActionPacingIdleBreakEnabled,
                source.ActionPacingIdleBreakIntervalMinMinutes,
                source.ActionPacingIdleBreakIntervalMaxMinutes,
                source.ActionPacingIdleBreakDurationMinMinutes,
                source.ActionPacingIdleBreakDurationMaxMinutes,
                source.ActionPacingIdleBrowseEnabled,
                source.ActionPacingIdleBrowseIntervalMinMinutes,
                source.ActionPacingIdleBrowseIntervalMaxMinutes,
                source.ActionPacingIdleBrowsePageMap,
                source.ActionPacingIdleBrowsePageStatistics,
                source.ActionPacingIdleBrowsePageStatisticsHero,
                source.ActionPacingIdleBrowsePageStatisticsTop10,
                source.ActionPacingIdleBrowsePageStatisticsDefenders,
                source.ActionPacingIdleBrowsePageStatisticsAttackers,
                source.ActionPacingIdleBrowsePageReports,
                source.ActionPacingIdleBrowsePageMessages),
        };

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

            if (key.Equals(BotOptionPayloadKeys.ActionPacingEnabled, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingTaskMinSeconds, out var taskMin))
            {
                result = result with { TaskMinSeconds = taskMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, out var taskMax))
            {
                result = result with { TaskMaxSeconds = taskMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, out var pageMin))
            {
                result = result with { PageLoadMinSeconds = pageMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, out var pageMax))
            {
                result = result with { PageLoadMaxSeconds = pageMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingClickMinSeconds, out var clickMin))
            {
                result = result with { ClickMinSeconds = clickMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingClickMaxSeconds, out var clickMax))
            {
                result = result with { ClickMaxSeconds = clickMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingLoopMinSeconds, out var loopMin))
            {
                result = result with { LoopMinSeconds = loopMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, out var loopMax))
            {
                result = result with { LoopMaxSeconds = loopMax };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.FarmListStepDelayMinSeconds, out var farmMin))
            {
                result = result with { FarmListStepMinSeconds = farmMin };
            }
            else if (TryReadDelay(key, value, BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, out var farmMax))
            {
                result = result with { FarmListStepMaxSeconds = farmMax };
            }
            else if (key.Equals(BotOptionPayloadKeys.ShortVillageDeferSeconds, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var shortVillageDeferSeconds))
            {
                result = result with
                {
                    ShortVillageDeferSeconds = PacingDefaults.NormalizeShortVillageDeferSeconds(shortVillageDeferSeconds),
                };
            }
            else if (key.Equals(BotOptionPayloadKeys.VillageRoundSleepExtensionMinutes, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var extensionMinutes))
            {
                result = result with
                {
                    VillageRoundSleepExtensionMinutes = PacingDefaults.NormalizeVillageRoundSleepExtensionMinutes(extensionMinutes),
                };
            }
        }

        return result;
    }

    internal static BotOptions ApplyTo(this ActionPacingOptions values, BotOptions source)
        => source with
        {
            HumanLikeEnabled = values.HumanLikeEnabled,
            HumanLikeSpeed = values.HumanLikeSpeed,
            ActionPacingEnabled = values.Enabled,
            ActionPacingTaskMinSeconds = values.TaskMinSeconds,
            ActionPacingTaskMaxSeconds = values.TaskMaxSeconds,
            ActionPacingPageLoadMinSeconds = values.PageLoadMinSeconds,
            ActionPacingPageLoadMaxSeconds = values.PageLoadMaxSeconds,
            ActionPacingClickMinSeconds = values.ClickMinSeconds,
            ActionPacingClickMaxSeconds = values.ClickMaxSeconds,
            ActionPacingLoopMinSeconds = values.LoopMinSeconds,
            ActionPacingLoopMaxSeconds = values.LoopMaxSeconds,
            FarmListStepDelayMinSeconds = values.FarmListStepMinSeconds,
            FarmListStepDelayMaxSeconds = Math.Max(values.FarmListStepMinSeconds, values.FarmListStepMaxSeconds),
            ShortVillageDeferSeconds = values.ShortVillageDeferSeconds,
            VillageRoundSleepExtensionMinutes = values.VillageRoundSleepExtensionMinutes,
            ContinuousKeepAliveEnabled = values.KeepAlive.Enabled,
            ContinuousKeepAliveMinMinutes = values.KeepAlive.MinMinutes,
            ContinuousKeepAliveMaxMinutes = values.KeepAlive.MaxMinutes,
            VillageStatusSweepEnabled = values.VillageStatusSweep.Enabled,
            VillageStatusSweepDorf1Enabled = values.VillageStatusSweep.Dorf1Enabled,
            VillageStatusSweepDorf2Enabled = values.VillageStatusSweep.Dorf2Enabled,
            VillageStatusSweepSmithyEnabled = values.VillageStatusSweep.SmithyEnabled,
            VillageStatusSweepBarracksEnabled = values.VillageStatusSweep.BarracksEnabled,
            VillageStatusSweepStableEnabled = values.VillageStatusSweep.StableEnabled,
            VillageStatusSweepWorkshopEnabled = values.VillageStatusSweep.WorkshopEnabled,
            VillageStatusSweepTownHallEnabled = values.VillageStatusSweep.TownHallEnabled,
            VillageStatusSweepBreweryEnabled = values.VillageStatusSweep.BreweryEnabled,
            VillageStatusSweepRoundMinMinutes = values.VillageStatusSweep.RoundMinMinutes,
            VillageStatusSweepRoundMaxMinutes = values.VillageStatusSweep.RoundMaxMinutes,
            VillageStatusSweepVillageMinSeconds = values.VillageStatusSweep.VillageMinSeconds,
            VillageStatusSweepVillageMaxSeconds = values.VillageStatusSweep.VillageMaxSeconds,
            ActionPacingIdleBreakEnabled = values.IdleActivity.BreakEnabled,
            ActionPacingIdleBreakIntervalMinMinutes = values.IdleActivity.BreakIntervalMinMinutes,
            ActionPacingIdleBreakIntervalMaxMinutes = values.IdleActivity.BreakIntervalMaxMinutes,
            ActionPacingIdleBreakDurationMinMinutes = values.IdleActivity.BreakDurationMinMinutes,
            ActionPacingIdleBreakDurationMaxMinutes = values.IdleActivity.BreakDurationMaxMinutes,
            ActionPacingIdleBrowseEnabled = values.IdleActivity.BrowseEnabled,
            ActionPacingIdleBrowseIntervalMinMinutes = values.IdleActivity.BrowseIntervalMinMinutes,
            ActionPacingIdleBrowseIntervalMaxMinutes = values.IdleActivity.BrowseIntervalMaxMinutes,
            ActionPacingIdleBrowsePageMap = values.IdleActivity.BrowseMap,
            ActionPacingIdleBrowsePageStatistics = values.IdleActivity.BrowseStatistics,
            ActionPacingIdleBrowsePageStatisticsHero = values.IdleActivity.BrowseStatisticsHero,
            ActionPacingIdleBrowsePageStatisticsTop10 = values.IdleActivity.BrowseStatisticsTop10,
            ActionPacingIdleBrowsePageStatisticsDefenders = values.IdleActivity.BrowseStatisticsDefenders,
            ActionPacingIdleBrowsePageStatisticsAttackers = values.IdleActivity.BrowseStatisticsAttackers,
            ActionPacingIdleBrowsePageReports = values.IdleActivity.BrowseReports,
            ActionPacingIdleBrowsePageMessages = values.IdleActivity.BrowseMessages,
        };

    private static double ReadDelay(IConfiguration configuration, string key, double defaultValue)
        => ClampDelaySeconds(configuration.GetValue(key, defaultValue));

    private static bool TryReadDelay(string key, string value, string expectedKey, out double delay)
    {
        delay = 0;
        if (!key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(value, out var parsed))
        {
            return false;
        }

        delay = ClampDelaySeconds(parsed);
        return true;
    }

    private static double ClampDelaySeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 3600);
    }
}
