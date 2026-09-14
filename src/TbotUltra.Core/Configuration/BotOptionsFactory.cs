using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

public static class BotOptionsFactory
{
    private const string DefaultHeroStatPriority = "resources,fighting_strength,offence_bonus,defence_bonus";

    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var tasks = configuration.GetSection("loop_tasks").Get<List<string>>() ?? ["status"];
        var continuousLoopGroups = configuration.GetSection("continuous_loop_groups").Get<List<string>>() ?? [];
        var continuousFarmListNames = configuration.GetSection(BotOptionPayloadKeys.ContinuousFarmListNames).Get<List<string>>() ?? [];
        var continuousFarmListIds = configuration.GetSection(BotOptionPayloadKeys.ContinuousFarmListIds).Get<List<string>>() ?? [];
        var resourceTransferSourceVillageNames = configuration.GetSection(BotOptionPayloadKeys.ResourceTransferSourceVillageNames).Get<List<string>>() ?? [];
        var reinforcementSourceVillageNames = configuration.GetSection(BotOptionPayloadKeys.ReinforcementsSourceVillageNames).Get<List<string>>() ?? [];
        var reinforcementTroopRules = NormalizeReinforcementTroopRules(
            configuration.GetSection(BotOptionPayloadKeys.ReinforcementsTroopRules).Get<List<ReinforcementTroopRule>>() ?? []);
        var reinforcementsSendIntervalHours = ReinforcementSendDefaults.NormalizeSendMinMinutes(
            configuration.GetValue(BotOptionPayloadKeys.ReinforcementsSendMinMinutes, ReinforcementSendDefaults.DefaultSendMinMinutes));
        var reinforcementsSendVariationPercent = ReinforcementSendDefaults.NormalizeSendMaxMinutes(
            configuration.GetValue(BotOptionPayloadKeys.ReinforcementsSendMaxMinutes, ReinforcementSendDefaults.DefaultSendMaxMinutes));
        var continuousFarmDispatchDelayMinutes = FarmingDefaults.NormalizeDispatchDelayMinMinutes(
            configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes, FarmingDefaults.DefaultDispatchDelayMinMinutes));
        var continuousFarmDispatchDelayVariationPercent = FarmingDefaults.NormalizeDispatchDelayMaxMinutes(
            configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes, FarmingDefaults.DefaultDispatchDelayMaxMinutes));
        var continuousFarmSendMode = FarmingDefaults.NormalizeSendMode(configuration[BotOptionPayloadKeys.ContinuousFarmSendMode]);
        var farmListOnlyCreateReportsWithLosses = configuration.GetValue(
            BotOptionPayloadKeys.FarmListOnlyCreateReportsWithLosses,
            FarmingDefaults.OnlyCreateReportsWithLosses);
        var showFarmListLastSentTimer = configuration.GetValue(BotOptionPayloadKeys.ShowFarmListLastSentTimer, FarmingDefaults.ShowLastSentTimer);
        var farmListLastSentLimitEnabled = configuration.GetValue(BotOptionPayloadKeys.FarmListLastSentLimitEnabled, FarmingDefaults.LastSentLimitEnabled);
        var farmListLastSentLimitHours = FarmingDefaults.NormalizeLastSentLimitHours(
            configuration.GetValue(BotOptionPayloadKeys.FarmListLastSentLimitHours, FarmingDefaults.DefaultLastSentLimitHours));
        var continuousFarmDeactivateLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateLosses, true);
        var configuredContinuousFarmMoveLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmMoveLosses, false);
        var continuousFarmMoveLosses = continuousFarmDeactivateLosses && configuredContinuousFarmMoveLosses;
        var continuousFarmDeactivateOasisLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses, false);
        var continuousFarmDeactivateRedLosses = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses, continuousFarmDeactivateLosses);
        var continuousFarmDeactivateYellowLosses = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses, continuousFarmDeactivateLosses);
        var continuousFarmDeactivateRedOasisLosses = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses, continuousFarmDeactivateOasisLosses);
        var continuousFarmDeactivateYellowOasisLosses = GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses, continuousFarmDeactivateOasisLosses);
        var continuousFarmMoveRedLosses = continuousFarmDeactivateRedLosses
            && GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmMoveRedLosses, continuousFarmMoveLosses);
        var continuousFarmMoveYellowLosses = continuousFarmDeactivateYellowLosses
            && GetValueOrDefault(configuration, BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses, continuousFarmMoveLosses);
        var legacyLossDestinationListId = configuration[BotOptionPayloadKeys.ContinuousFarmLossDestinationListId] ?? string.Empty;
        var legacyLossDestinationListName = configuration[BotOptionPayloadKeys.ContinuousFarmLossDestinationListName] ?? string.Empty;
        var legacyLossDestinationBaseName = configuration[BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName] ?? string.Empty;
        var townHallCelebrationMode = TownHallCelebrationDefaults.NormalizeMode(configuration[BotOptionPayloadKeys.TownHallCelebrationMode]);
        var townHallCelebrationCount = TownHallCelebrationDefaults.NormalizeCount(
            configuration.GetValue(BotOptionPayloadKeys.TownHallCelebrationCount, TownHallCelebrationDefaults.DefaultCount));
        var townHallCelebrationRestartDelayMinMinutes = configuration.GetValue(
            BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes, TownHallCelebrationDefaults.DefaultRestartDelayMinMinutes);
        var townHallCelebrationRestartDelayMaxMinutes = configuration.GetValue(
            BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes, TownHallCelebrationDefaults.DefaultRestartDelayMaxMinutes);
        var breweryCelebrationRestartDelayMinMinutes = configuration.GetValue(
            BotOptionPayloadKeys.BreweryCelebrationRestartDelayMinMinutes, BreweryCelebrationDefaults.DefaultRestartDelayMinMinutes);
        var breweryCelebrationRestartDelayMaxMinutes = configuration.GetValue(
            BotOptionPayloadKeys.BreweryCelebrationRestartDelayMaxMinutes, BreweryCelebrationDefaults.DefaultRestartDelayMaxMinutes);
        var townHallCelebrationRestartDelayEnabled = configuration.GetValue(
            BotOptionPayloadKeys.TownHallCelebrationRestartDelayEnabled, TownHallCelebrationDefaults.DefaultRestartDelayEnabled);
        var breweryCelebrationRestartDelayEnabled = configuration.GetValue(
            BotOptionPayloadKeys.BreweryCelebrationRestartDelayEnabled, BreweryCelebrationDefaults.DefaultRestartDelayEnabled);

        var baseUrl = (configuration["base_url"] ?? string.Empty).TrimEnd('/');
        var heroStatPriority = string.IsNullOrWhiteSpace(configuration[BotOptionPayloadKeys.HeroStatPriority])
            ? DefaultHeroStatPriority
            : configuration[BotOptionPayloadKeys.HeroStatPriority]!;
        var heroStatMaximums = HeroAttributeMaximums.Serialize(
            HeroAttributeMaximums.Parse(configuration[BotOptionPayloadKeys.HeroStatMaximums]));
        var legacyActionPacing = LegacyActionPacingCompatibility.Resolve(configuration);
        var actionPacingDefaults = legacyActionPacing.Fallbacks;

        return new BotOptions
        {
            ServerName = configuration["server_name"] ?? string.Empty,
            BaseUrl = baseUrl,
            TimeoutMs = configuration.GetValue("timeout_ms", 20000),
            ManualLoginTimeoutSeconds = configuration.GetValue("manual_login_timeout_seconds", 180),
            LoopIntervalSeconds = configuration.GetValue("loop_interval_seconds", 60),
            LoopTasks = tasks,
            ContinuousLoopGroups = continuousLoopGroups,
            ContinuousFarmListNames = continuousFarmListNames,
            ContinuousFarmListIds = continuousFarmListIds,
            ContinuousFarmDispatchDelayMinMinutes = continuousFarmDispatchDelayMinutes,
            ContinuousFarmDispatchDelayMaxMinutes = continuousFarmDispatchDelayVariationPercent,
            ContinuousFarmSendMode = continuousFarmSendMode,
            FarmListOnlyCreateReportsWithLosses = farmListOnlyCreateReportsWithLosses,
            ShowFarmListLastSentTimer = showFarmListLastSentTimer,
            FarmListLastSentLimitEnabled = farmListLastSentLimitEnabled,
            FarmListLastSentLimitHours = farmListLastSentLimitHours,
            TownHallCelebrationMode = townHallCelebrationMode,
            TownHallCelebrationCount = townHallCelebrationCount,
            TownHallCelebrationRestartDelayMinMinutes = townHallCelebrationRestartDelayMinMinutes,
            TownHallCelebrationRestartDelayMaxMinutes = townHallCelebrationRestartDelayMaxMinutes,
            TownHallCelebrationRestartDelayEnabled = townHallCelebrationRestartDelayEnabled,
            BreweryCelebrationRestartDelayMinMinutes = breweryCelebrationRestartDelayMinMinutes,
            BreweryCelebrationRestartDelayMaxMinutes = breweryCelebrationRestartDelayMaxMinutes,
            BreweryCelebrationRestartDelayEnabled = breweryCelebrationRestartDelayEnabled,
            HeroAdventureRestartDelayEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroAdventureRestartDelayEnabled, HeroAdventureRestartDelayDefaults.Enabled),
            HeroAdventureRestartDelayMinMinutes = configuration.GetValue(BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes, HeroAdventureRestartDelayDefaults.MinMinutes),
            HeroAdventureRestartDelayMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes, HeroAdventureRestartDelayDefaults.MaxMinutes),
            SmithyUpgradeRestartDelayEnabled = configuration.GetValue(BotOptionPayloadKeys.SmithyUpgradeRestartDelayEnabled, SmithyUpgradeRestartDelayDefaults.Enabled),
            SmithyUpgradeRestartDelayMinMinutes = configuration.GetValue(BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes, SmithyUpgradeRestartDelayDefaults.MinMinutes),
            SmithyUpgradeRestartDelayMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes, SmithyUpgradeRestartDelayDefaults.MaxMinutes),
            ContinuousFarmDeactivateLosses = continuousFarmDeactivateLosses,
            ContinuousFarmDeactivateOasisLosses = continuousFarmDeactivateOasisLosses,
            ContinuousFarmMoveLosses = continuousFarmMoveLosses,
            ContinuousFarmLossDestinationListId = legacyLossDestinationListId,
            ContinuousFarmLossDestinationListName = legacyLossDestinationListName,
            ContinuousFarmLossDestinationBaseName = legacyLossDestinationBaseName,
            ContinuousFarmDeactivateRedLosses = continuousFarmDeactivateRedLosses,
            ContinuousFarmDeactivateYellowLosses = continuousFarmDeactivateYellowLosses,
            ContinuousFarmDeactivateRedOasisLosses = continuousFarmDeactivateRedOasisLosses,
            ContinuousFarmDeactivateYellowOasisLosses = continuousFarmDeactivateYellowOasisLosses,
            ContinuousFarmMoveRedLosses = continuousFarmMoveRedLosses,
            ContinuousFarmMoveYellowLosses = continuousFarmMoveYellowLosses,
            ContinuousFarmRedLossDestinationListId = configuration[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId] ?? legacyLossDestinationListId,
            ContinuousFarmRedLossDestinationListName = configuration[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName] ?? legacyLossDestinationListName,
            ContinuousFarmRedLossDestinationBaseName = configuration[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName] ?? legacyLossDestinationBaseName,
            ContinuousFarmYellowLossDestinationListId = configuration[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId] ?? legacyLossDestinationListId,
            ContinuousFarmYellowLossDestinationListName = configuration[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName] ?? legacyLossDestinationListName,
            ContinuousFarmYellowLossDestinationBaseName = configuration[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName] ?? legacyLossDestinationBaseName,
            PostLoginAnalyzeFarmlists = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists, false),
            PostLoginAnalyzeHero = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHero, false),
            PostLoginAnalyzeHeroInventory = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory, false),
            PostLoginReadTroopTrainingQueue = configuration.GetValue(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue, false),
            PostLoginAnalyzeBrewery = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeBrewery, false),
            PostLoginAnalyzeNewVillages = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeNewVillages, true),
            PostLoginAnalyzeNewAccount = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeNewAccount, true),
            AutomaticallyCheckLanguage = configuration.GetValue(BotOptionPayloadKeys.AutomaticallyCheckLanguage, true),
            DetailedBrowserLoggingEnabled = configuration.GetValue(BotOptionPayloadKeys.DetailedBrowserLoggingEnabled, false),
            TurnOffVideoSound = configuration.GetValue(BotOptionPayloadKeys.TurnOffVideoSound, true),
            TroopTrainingBarracksEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, false),
            TroopTrainingBarracksTroopType = configuration[BotOptionPayloadKeys.TroopTrainingBarracksTroopType] ?? string.Empty,
            TroopTrainingBarracksMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] ?? "no_limit",
            TroopTrainingBarracksAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] ?? "maximum",
            TroopTrainingBarracksKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, 10), 0, 95),
            TroopTrainingBarracksRunMode = NormalizeTroopTrainingRunMode(configuration[BotOptionPayloadKeys.TroopTrainingBarracksRunMode]),
            TroopTrainingBarracksMinimumTroopsEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroopsEnabled, false),
            TroopTrainingBarracksMinimumTroops = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, 20), 1, 10000),
            TroopTrainingBarracksMaximumMinimumTroops = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMaximumMinimumTroops, 100), 1, 10000),
            TroopTrainingBarracksMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, 90), 0, 100),
            TroopTrainingBarracksTimedMinMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes, 30)),
            TroopTrainingBarracksTimedMaxMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes, 120)),
            TroopTrainingBarracksCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, true),
            TroopTrainingBarracksCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, true),
            TroopTrainingBarracksCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, true),
            TroopTrainingBarracksCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop, false),
            TroopTrainingBarracksAutomaticResourceSelection = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksAutomaticResourceSelection, false),
            TroopTrainingStableEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableEnabled, false),
            TroopTrainingStableTroopType = configuration[BotOptionPayloadKeys.TroopTrainingStableTroopType] ?? string.Empty,
            TroopTrainingStableMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours] ?? "no_limit",
            TroopTrainingStableAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingStableAmountMode] ?? "maximum",
            TroopTrainingStableKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, 10), 0, 95),
            TroopTrainingStableRunMode = NormalizeTroopTrainingRunMode(configuration[BotOptionPayloadKeys.TroopTrainingStableRunMode]),
            TroopTrainingStableMinimumTroopsEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumTroopsEnabled, false),
            TroopTrainingStableMinimumTroops = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, 20), 1, 10000),
            TroopTrainingStableMaximumMinimumTroops = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMaximumMinimumTroops, 100), 1, 10000),
            TroopTrainingStableMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, 90), 0, 100),
            TroopTrainingStableTimedMinMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes, 30)),
            TroopTrainingStableTimedMaxMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes, 120)),
            TroopTrainingStableCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckWood, true),
            TroopTrainingStableCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckClay, true),
            TroopTrainingStableCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckIron, true),
            TroopTrainingStableCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckCrop, false),
            TroopTrainingStableAutomaticResourceSelection = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableAutomaticResourceSelection, false),
            TroopTrainingWorkshopEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, false),
            TroopTrainingWorkshopTroopType = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopTroopType] ?? string.Empty,
            TroopTrainingWorkshopMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours] ?? "no_limit",
            TroopTrainingWorkshopAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode] ?? "maximum",
            TroopTrainingWorkshopKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, 10), 0, 95),
            TroopTrainingWorkshopRunMode = NormalizeTroopTrainingRunMode(configuration[BotOptionPayloadKeys.TroopTrainingWorkshopRunMode]),
            TroopTrainingWorkshopMinimumTroopsEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroopsEnabled, false),
            TroopTrainingWorkshopMinimumTroops = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, 20), 1, 10000),
            TroopTrainingWorkshopMaximumMinimumTroops = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMaximumMinimumTroops, 100), 1, 10000),
            TroopTrainingWorkshopMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, 90), 0, 100),
            TroopTrainingWorkshopTimedMinMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes, 30)),
            TroopTrainingWorkshopTimedMaxMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes, 120)),
            TroopTrainingWorkshopCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, true),
            TroopTrainingWorkshopCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, true),
            TroopTrainingWorkshopCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, true),
            TroopTrainingWorkshopCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop, false),
            TroopTrainingWorkshopAutomaticResourceSelection = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopAutomaticResourceSelection, false),
            TroopTrainingFallbackCooldownSeconds = ClampTroopTrainingFallbackCooldownSeconds(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, 120)),
            BreweryAutoCelebrationEnabled = configuration.GetValue(BotOptionPayloadKeys.BreweryAutoCelebrationEnabled, false),
            NpcTradeEnabled = GetValueOrDefault(configuration, BotOptionPayloadKeys.NpcTradeEnabled, defaultValue: false),
            NpcTradeConstructionEnabled = GetValueOrDefault(configuration, BotOptionPayloadKeys.NpcTradeConstructionEnabled, defaultValue: false),
            NpcTradeThresholdPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.NpcTradeThresholdPercent, 90), 1, 100),
            NpcTradeAnalyzeWood = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeWood, true),
            NpcTradeAnalyzeClay = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeClay, true),
            NpcTradeAnalyzeIron = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeIron, true),
            NpcTradeAnalyzeCrop = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeCrop, true),
            NpcTradeBuildTimeLimitEnabled = configuration.GetValue(BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled, true),
            NpcTradeBuildTimeLimitSeconds = configuration.GetValue(BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds, 300),
            ResourceTransferEnabled = configuration.GetValue(BotOptionPayloadKeys.ResourceTransferEnabled, false),
            ResourceTransferTargetVillageName = configuration[BotOptionPayloadKeys.ResourceTransferTargetVillageName] ?? string.Empty,
            ResourceTransferSourceVillageNames = resourceTransferSourceVillageNames,
            ResourceTransferSourceThresholdPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent, 50), 0, 100),
            ResourceTransferSourceKeepPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSourceKeepPercent, 5), 0, 99),
            ResourceTransferTargetFillPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ResourceTransferTargetFillPercent, 90), 0, 100),
            ResourceTransferSendWood = configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendWood, true),
            ResourceTransferSendClay = configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendClay, true),
            ResourceTransferSendIron = configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendIron, true),
            ResourceTransferSendCrop = configuration.GetValue(BotOptionPayloadKeys.ResourceTransferSendCrop, true),
            ReinforcementsEnabled = configuration.GetValue(BotOptionPayloadKeys.ReinforcementsEnabled, false),
            ReinforcementsTargetVillageName = configuration[BotOptionPayloadKeys.ReinforcementsTargetVillageName] ?? string.Empty,
            ReinforcementsSourceVillageNames = reinforcementSourceVillageNames,
            ReinforcementsTroopRules = reinforcementTroopRules,
            ReinforcementsSendMinMinutes = reinforcementsSendIntervalHours,
            ReinforcementsSendMaxMinutes = reinforcementsSendVariationPercent,
            GithubReleasesUrl = configuration["github_releases_url"] ?? string.Empty,
            HumanLikeEnabled = legacyActionPacing.HumanLikeEnabled,
            HumanLikeSpeed = legacyActionPacing.HumanLikeSpeed,
            ActionPacingEnabled = legacyActionPacing.ActionPacingEnabled,
            ActionPacingTaskMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingTaskMinSeconds, actionPacingDefaults.TaskMinSeconds)),
            ActionPacingTaskMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, actionPacingDefaults.TaskMaxSeconds)),
            ActionPacingPageLoadMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, actionPacingDefaults.PageLoadMinSeconds)),
            ActionPacingPageLoadMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, actionPacingDefaults.PageLoadMaxSeconds)),
            ActionPacingClickMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingClickMinSeconds, actionPacingDefaults.ClickMinSeconds)),
            ActionPacingClickMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingClickMaxSeconds, actionPacingDefaults.ClickMaxSeconds)),
            ActionPacingLoopMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingLoopMinSeconds, actionPacingDefaults.LoopMinSeconds)),
            ActionPacingLoopMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, actionPacingDefaults.LoopMaxSeconds)),
            ShortVillageDeferSeconds = PacingDefaults.NormalizeShortVillageDeferSeconds(
                configuration.GetValue(BotOptionPayloadKeys.ShortVillageDeferSeconds, PacingDefaults.ShortVillageDeferSeconds)),
            ContinuousKeepAliveEnabled = configuration.GetValue(BotOptionPayloadKeys.ContinuousKeepAliveEnabled, PacingDefaults.ContinuousKeepAliveEnabled),
            ContinuousKeepAliveMinMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ContinuousKeepAliveMinMinutes, PacingDefaults.ContinuousKeepAliveMinMinutes), 1, 1440),
            ContinuousKeepAliveMaxMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ContinuousKeepAliveMaxMinutes, PacingDefaults.ContinuousKeepAliveMaxMinutes), 1, 1440),
            VillageStatusSweepEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepEnabled, PacingDefaults.VillageStatusSweepEnabled),
            VillageStatusSweepDorf1Enabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepDorf1Enabled, true),
            VillageStatusSweepDorf2Enabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepDorf2Enabled, false),
            VillageStatusSweepSmithyEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepSmithyEnabled, false),
            VillageStatusSweepBarracksEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepBarracksEnabled, false),
            VillageStatusSweepStableEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepStableEnabled, false),
            VillageStatusSweepWorkshopEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepWorkshopEnabled, false),
            VillageStatusSweepTownHallEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepTownHallEnabled, false),
            VillageStatusSweepBreweryEnabled = configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepBreweryEnabled, false),
            VillageStatusSweepRoundMinMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepRoundMinMinutes, PacingDefaults.VillageStatusSweepRoundMinMinutes), 1, 1440),
            VillageStatusSweepRoundMaxMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepRoundMaxMinutes, PacingDefaults.VillageStatusSweepRoundMaxMinutes), 1, 1440),
            VillageStatusSweepVillageMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepVillageMinSeconds, PacingDefaults.VillageStatusSweepVillageMinSeconds)),
            VillageStatusSweepVillageMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.VillageStatusSweepVillageMaxSeconds, PacingDefaults.VillageStatusSweepVillageMaxSeconds)),
            FarmListStepDelayMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, actionPacingDefaults.FarmListMinSeconds)),
            FarmListStepDelayMaxSeconds = Math.Max(
                ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, actionPacingDefaults.FarmListMinSeconds)),
                ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, actionPacingDefaults.FarmListMaxSeconds))),
            ActionPacingIdleBreakEnabled = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakEnabled, PacingDefaults.ActionPacingIdleBreakEnabled),
            ActionPacingIdleBreakIntervalMinMinutes = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes, PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes),
            ActionPacingIdleBreakIntervalMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes, PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes),
            ActionPacingIdleBreakDurationMinMinutes = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes, PacingDefaults.ActionPacingIdleBreakDurationMinMinutes),
            ActionPacingIdleBreakDurationMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes, PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes),
            ActionPacingIdleBrowseEnabled = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled, PacingDefaults.ActionPacingIdleBrowseEnabled),
            ActionPacingIdleBrowseIntervalMinMinutes = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes, PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes),
            ActionPacingIdleBrowseIntervalMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes, PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes),
            ActionPacingIdleBrowsePageMap = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap, PacingDefaults.ActionPacingIdleBrowsePageMap),
            ActionPacingIdleBrowsePageStatistics = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics, PacingDefaults.ActionPacingIdleBrowsePageStatistics),
            ActionPacingIdleBrowsePageStatisticsHero = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero, PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero),
            ActionPacingIdleBrowsePageStatisticsTop10 = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10, PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10),
            ActionPacingIdleBrowsePageStatisticsDefenders = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders, PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders),
            ActionPacingIdleBrowsePageStatisticsAttackers = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers, PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers),
            ActionPacingIdleBrowsePageReports = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports, PacingDefaults.ActionPacingIdleBrowsePageReports),
            ActionPacingIdleBrowsePageMessages = configuration.GetValue(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages, PacingDefaults.ActionPacingIdleBrowsePageMessages),
            ConstructionHumanizeDelayEnabled = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled, PacingDefaults.ConstructionHumanizeDelayEnabled),
            ConstructionStorageUpgradeLevelsAhead = ConstructionDefaults.NormalizeStorageUpgradeLevelsAhead(
                configuration.GetValue(BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead, ConstructionDefaults.StorageUpgradeLevelsAhead)),
            ConstructionCropShortageRecoveryEnabled = configuration.GetValue(
                BotOptionPayloadKeys.ConstructionCropShortageRecoveryEnabled,
                ConstructionDefaults.CropShortageRecoveryEnabled),
            ConstructionHumanizeStateVersion = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeStateVersion, 0),
            ConstructionHumanizeQueuePercentMin = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin, PacingDefaults.ConstructionHumanizeQueuePercentMin),
            ConstructionHumanizeQueuePercentMax = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax, PacingDefaults.ConstructionHumanizeQueuePercentMax),
            ConstructionHumanizeMaxDelayMinutes = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes, PacingDefaults.ConstructionHumanizeMaxDelayMinutes),
            ConstructionHumanizeNoPlusMinMinutes = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes, PacingDefaults.ConstructionHumanizeNoPlusMinMinutes),
            ConstructionHumanizeNoPlusMaxMinutes = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes, PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes),
            ConstructionPreSleepFill = configuration.GetValue(BotOptionPayloadKeys.ConstructionPreSleepFill, false),
            ConstructionLoginFill = configuration.GetValue(BotOptionPayloadKeys.ConstructionLoginFill, false),
            ConstructionLoginFillExpiresAtUnixSeconds = configuration.GetValue<long?>(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds),
            ConstructionHumanizePreNavigationDelaySatisfied = configuration.GetValue(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied, false),
            TargetVillageName = configuration[BotOptionPayloadKeys.TargetVillageName] ?? string.Empty,
            TargetVillageUrl = configuration[BotOptionPayloadKeys.TargetVillageUrl] ?? string.Empty,
            TargetVillageKey = configuration[BotOptionPayloadKeys.TargetVillageKey] ?? string.Empty,
            AllowGoldSpending = GetValueOrDefault(configuration, BotOptionPayloadKeys.AllowGoldSpending, defaultValue: false),
            AllowSilverSpending = configuration.GetValue("allow_silver_spending", false),
            GoldLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.GoldLimit, 100)),
            DailyGoldSpendingLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.DailyGoldSpendingLimit, 20)),
            SilverLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.SilverLimit, 100)),
            DailySilverSpendingLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.DailySilverSpendingLimit, 10000)),
            ResourceUpgradeSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeSlotId),
            ResourceUpgradeTargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeTargetLevel),
            ResourceUpgradeMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, 30),
            ResourceBuildStrategy = configuration[BotOptionPayloadKeys.ResourceBuildStrategy] ?? "lowest_first",
            SmithyUpgradeTargets = configuration[BotOptionPayloadKeys.SmithyUpgradeTargets],
            BuildingUpgradeSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeSlotId),
            BuildingUpgradeTargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeTargetLevel),
            BuildingUpgradeName = configuration[BotOptionPayloadKeys.BuildingUpgradeName] ?? string.Empty,
            BuildingUpgradeMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.BuildingUpgradeMaxAttempts, 30),
            BuildingConstructSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructSlotId),
            BuildingConstructGid = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructGid),
            BuildingConstructName = configuration[BotOptionPayloadKeys.BuildingConstructName] ?? string.Empty,
            BuildingConstructAllowSlotFallback = configuration.GetValue(BotOptionPayloadKeys.BuildingConstructAllowSlotFallback, false),
            BuildingConstructFallbackExcludedSlots = configuration[BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots] ?? string.Empty,
            ConstructFasterEnabled = configuration.GetValue(BotOptionPayloadKeys.ConstructFasterEnabled, true),
            ConstructFasterMinBuildTimeEnabled = configuration.GetValue(BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled, true),
            ConstructFasterMinBuildMinutes = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes, 30)),
            ConstructFasterRandomEnabled = configuration.GetValue(BotOptionPayloadKeys.ConstructFasterRandomEnabled, false),
            ConstructFasterRandomChancePercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ConstructFasterRandomChancePercent, 50), 0, 100),
            TargetBuildingSlotOrName = configuration[BotOptionPayloadKeys.TargetBuildingSlotOrName] ?? string.Empty,
            TargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.TargetLevel),
            DemolishDelayMinMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.DemolishDelayMinMinutes, DemolishDefaults.DefaultDelayMinMinutes), 0, 1440),
            DemolishDelayMaxMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.DemolishDelayMaxMinutes, DemolishDefaults.DefaultDelayMaxMinutes), 0, 1440),
            HeroMinHpForAdventure = configuration.GetValue(BotOptionPayloadKeys.HeroMinHpForAdventure, 50),
            HeroHpRegenPerDayPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroHpRegenPerDayPercent, 40), 20, 100),
            HeroAutoRevive = configuration.GetValue(BotOptionPayloadKeys.HeroAutoRevive, false),
            HeroAutoAssignPoints = configuration.GetValue(BotOptionPayloadKeys.HeroAutoAssignPoints, false),
            HeroAutoUseOintments = configuration.GetValue(BotOptionPayloadKeys.HeroAutoUseOintments, false),
            HeroOintmentTargetHpPercent = NormalizeHeroOintmentTarget(configuration.GetValue(BotOptionPayloadKeys.HeroOintmentTargetHpPercent, 100)),
            HeroStatPriority = heroStatPriority,
            HeroStatMaximums = heroStatMaximums,
            HeroAdventurePickOrder = configuration[BotOptionPayloadKeys.HeroAdventurePickOrder] ?? "shortest",
            HeroContinuousAdventures = configuration.GetValue(BotOptionPayloadKeys.HeroContinuousAdventures, false),
            IncreaseAdventuresToHard = configuration.GetValue(BotOptionPayloadKeys.IncreaseAdventuresToHard, true),
            ReduceAdventureTime = configuration.GetValue(BotOptionPayloadKeys.ReduceAdventureTime, false),
            HeroAdventureVideoChancePercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroAdventureVideoChancePercent, 70), 0, 100),
            AutoCollectTasksEnabled = configuration.GetValue(BotOptionPayloadKeys.AutoCollectTasksEnabled, true),
            AutoCollectDailyQuestsEnabled = configuration.GetValue(BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled, true),
            ProductionBonusVideoEnabled = configuration.GetValue(BotOptionPayloadKeys.ProductionBonusVideoEnabled, true),
            CollectStepDelayMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMinSeconds, PacingDefaults.CollectStepDelayMinSeconds)),
            CollectStepDelayMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMaxSeconds, PacingDefaults.CollectStepDelayMaxSeconds)),
            HeroResourceTransferEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroResourceTransferEnabled, true),
            HeroResourceMaxUseEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUseEnabled, true),
            HeroResourceMaxUsePerResource = configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUsePerResource, 5000),
            HeroResourceUseConstruction = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseConstruction, true),
            HeroResourceUseSmithy = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseSmithy, false),
            HeroResourceUseBrewery = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseBrewery, false),
            HeroResourceUseTownHall = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseTownHall, false),
            HeroCropAntiStarveEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveEnabled, HeroCropAntiStarveDefaults.Enabled),
            HeroCropAntiStarveTriggerMinutes = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveTriggerMinutes, HeroCropAntiStarveDefaults.TriggerMinutes),
            HeroCropAntiStarveTargetMinutes = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveTargetMinutes, HeroCropAntiStarveDefaults.TargetMinutes),
            HeroCropAntiStarveMaxCropPerTransfer = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveMaxCropPerTransfer, HeroCropAntiStarveDefaults.MaxCropPerTransfer),
            HeroCropAntiStarveMinHeroCropRemaining = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveMinHeroCropRemaining, HeroCropAntiStarveDefaults.MinHeroCropRemaining),
            UpgradeSelectorProfile = configuration[BotOptionPayloadKeys.UpgradeSelectorProfile] ?? "auto",
        };
    }

    private static int NormalizeHeroOintmentTarget(int value)
        => value is 50 or 60 or 70 or 80 or 90 or 100 ? value : 100;

    public static BotOptions CloneWithOverrides(
        BotOptions source,
        int? resourceUpgradeTargetLevelOverride = null,
        string? targetVillageNameOverride = null,
        string? targetVillageUrlOverride = null)
    {
        // BotOptions is a record: `with` copies every property from source, so the
        // only fields we name are the three optional overrides. This makes it impossible
        // to silently drop a field when a new setting is added (the old hand-written copy
        // list did exactly that for NpcTradeBuildTimeLimit*).
        return source with
        {
            TargetVillageName = targetVillageNameOverride ?? source.TargetVillageName,
            TargetVillageUrl = targetVillageUrlOverride ?? source.TargetVillageUrl,
            ResourceUpgradeTargetLevel = resourceUpgradeTargetLevelOverride ?? source.ResourceUpgradeTargetLevel,
        };
    }

    private static int ClampTroopTrainingFallbackCooldownSeconds(int value)
    {
        return value switch
        {
            10 or 30 or 60 or 120 or 300 or 600 => value,
            _ => 30,
        };
    }

    private static bool GetValueOrDefault(IConfiguration configuration, string key, bool defaultValue)
    {
        return configuration[key] is null
            ? defaultValue
            : configuration.GetValue(key, defaultValue);
    }

    private static double ClampDelaySeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 3600);
    }

    private static string NormalizeTroopTrainingRunMode(string? value)
        => string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase)
            ? "resource_percent"
            : "timed";

    private static List<ReinforcementTroopRule> NormalizeReinforcementTroopRules(IEnumerable<ReinforcementTroopRule> rules)
    {
        return rules
            .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.TroopType))
            .Select(rule => rule.Normalize())
            .GroupBy(rule => $"{rule.AccountName}\u001f{rule.SourceVillageName}\u001f{rule.TroopType}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

}
