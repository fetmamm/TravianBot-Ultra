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
        var townHallCelebrationMode = TownHallCelebrationDefaults.NormalizeMode(configuration[BotOptionPayloadKeys.TownHallCelebrationMode]);

        var baseUrl = (configuration["base_url"] ?? string.Empty).TrimEnd('/');
        var heroStatPriority = string.IsNullOrWhiteSpace(configuration[BotOptionPayloadKeys.HeroStatPriority])
            ? DefaultHeroStatPriority
            : configuration[BotOptionPayloadKeys.HeroStatPriority]!;
        var legacyHumanLikeEnabled = configuration.GetValue("human_like_enabled", false);
        var legacyHumanLikeSpeed = configuration["human_like_speed"] ?? "medium";
        var actionPacingEnabled = ResolveActionPacingEnabled(configuration, legacyHumanLikeEnabled);
        var actionPacingDefaults = ResolveActionPacingFallbacks(legacyHumanLikeEnabled, legacyHumanLikeSpeed);

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
            TownHallCelebrationMode = townHallCelebrationMode,
            ContinuousFarmDeactivateLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateLosses, false),
            ContinuousFarmDeactivateOasisLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses, false),
            PostLoginAnalyzeFarmlists = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists, false),
            PostLoginAnalyzeHero = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHero, false),
            PostLoginAnalyzeHeroInventory = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory, true),
            PostLoginReadTroopTrainingQueue = configuration.GetValue(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue, false),
            PostLoginAnalyzeBrewery = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeBrewery, false),
            PostLoginAnalyzeNewVillages = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeNewVillages, true),
            TroopTrainingBarracksEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksEnabled, false),
            TroopTrainingBarracksTroopType = configuration[BotOptionPayloadKeys.TroopTrainingBarracksTroopType] ?? string.Empty,
            TroopTrainingBarracksMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] ?? "no_limit",
            TroopTrainingBarracksAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] ?? "maximum",
            TroopTrainingBarracksKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent, 10), 0, 95),
            TroopTrainingBarracksRunMode = NormalizeTroopTrainingRunMode(configuration[BotOptionPayloadKeys.TroopTrainingBarracksRunMode]),
            TroopTrainingBarracksMinimumTroops = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, 1)),
            TroopTrainingBarracksMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, 50), 0, 100),
            TroopTrainingBarracksTimedMinMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes, 30)),
            TroopTrainingBarracksTimedMaxMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes, 120)),
            TroopTrainingBarracksCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, true),
            TroopTrainingBarracksCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, true),
            TroopTrainingBarracksCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, true),
            TroopTrainingBarracksCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop, true),
            TroopTrainingStableEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableEnabled, false),
            TroopTrainingStableTroopType = configuration[BotOptionPayloadKeys.TroopTrainingStableTroopType] ?? string.Empty,
            TroopTrainingStableMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours] ?? "no_limit",
            TroopTrainingStableAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingStableAmountMode] ?? "maximum",
            TroopTrainingStableKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, 10), 0, 95),
            TroopTrainingStableRunMode = NormalizeTroopTrainingRunMode(configuration[BotOptionPayloadKeys.TroopTrainingStableRunMode]),
            TroopTrainingStableMinimumTroops = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, 1)),
            TroopTrainingStableMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, 50), 0, 100),
            TroopTrainingStableTimedMinMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes, 30)),
            TroopTrainingStableTimedMaxMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes, 120)),
            TroopTrainingStableCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckWood, true),
            TroopTrainingStableCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckClay, true),
            TroopTrainingStableCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckIron, true),
            TroopTrainingStableCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckCrop, true),
            TroopTrainingWorkshopEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, false),
            TroopTrainingWorkshopTroopType = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopTroopType] ?? string.Empty,
            TroopTrainingWorkshopMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours] ?? "no_limit",
            TroopTrainingWorkshopAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode] ?? "maximum",
            TroopTrainingWorkshopKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, 10), 0, 95),
            TroopTrainingWorkshopRunMode = NormalizeTroopTrainingRunMode(configuration[BotOptionPayloadKeys.TroopTrainingWorkshopRunMode]),
            TroopTrainingWorkshopMinimumTroops = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, 1)),
            TroopTrainingWorkshopMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, 50), 0, 100),
            TroopTrainingWorkshopTimedMinMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes, 30)),
            TroopTrainingWorkshopTimedMaxMinutes = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes, 120)),
            TroopTrainingWorkshopCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, true),
            TroopTrainingWorkshopCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, true),
            TroopTrainingWorkshopCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, true),
            TroopTrainingWorkshopCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop, true),
            TroopTrainingFallbackCooldownSeconds = ClampTroopTrainingFallbackCooldownSeconds(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, 120)),
            NpcTradeEnabled = GetValueOrDefault(configuration, BotOptionPayloadKeys.NpcTradeEnabled, defaultValue: false),
            NpcTradeConstructionEnabled = GetValueOrDefault(configuration, BotOptionPayloadKeys.NpcTradeConstructionEnabled, defaultValue: false),
            NpcTradeThresholdPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.NpcTradeThresholdPercent, 90), 1, 100),
            NpcTradeAnalyzeWood = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeWood, true),
            NpcTradeAnalyzeClay = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeClay, true),
            NpcTradeAnalyzeIron = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeIron, true),
            NpcTradeAnalyzeCrop = configuration.GetValue(BotOptionPayloadKeys.NpcTradeAnalyzeCrop, true),
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
            HumanLikeEnabled = legacyHumanLikeEnabled,
            HumanLikeSpeed = legacyHumanLikeSpeed,
            ActionPacingEnabled = actionPacingEnabled,
            ActionPacingTaskMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingTaskMinSeconds, actionPacingDefaults.TaskMinSeconds)),
            ActionPacingTaskMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, actionPacingDefaults.TaskMaxSeconds)),
            ActionPacingPageLoadMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, actionPacingDefaults.PageLoadMinSeconds)),
            ActionPacingPageLoadMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, actionPacingDefaults.PageLoadMaxSeconds)),
            ActionPacingClickMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingClickMinSeconds, actionPacingDefaults.ClickMinSeconds)),
            ActionPacingClickMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingClickMaxSeconds, actionPacingDefaults.ClickMaxSeconds)),
            ActionPacingLoopMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingLoopMinSeconds, actionPacingDefaults.LoopMinSeconds)),
            ActionPacingLoopMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, actionPacingDefaults.LoopMaxSeconds)),
            FarmListStepDelayMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, actionPacingDefaults.FarmListMinSeconds)),
            FarmListStepDelayMaxSeconds = Math.Max(
                ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, actionPacingDefaults.FarmListMinSeconds)),
                ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, actionPacingDefaults.FarmListMaxSeconds))),
            TargetVillageName = configuration[BotOptionPayloadKeys.TargetVillageName] ?? string.Empty,
            TargetVillageUrl = configuration[BotOptionPayloadKeys.TargetVillageUrl] ?? string.Empty,
            AllowGoldSpending = GetValueOrDefault(configuration, BotOptionPayloadKeys.AllowGoldSpending, defaultValue: false),
            AllowSilverSpending = configuration.GetValue("allow_silver_spending", false),
            GoldLimit = configuration.GetValue(BotOptionPayloadKeys.GoldLimit, 300),
            SilverLimit = configuration.GetValue("silver_limit", 100),
            ResourceUpgradeSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeSlotId),
            ResourceUpgradeTargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeTargetLevel),
            ResourceUpgradeMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, 30),
            ResourceBuildStrategy = configuration[BotOptionPayloadKeys.ResourceBuildStrategy] ?? "smart",
            SmithyUpgradeTargets = configuration[BotOptionPayloadKeys.SmithyUpgradeTargets],
            BuildingUpgradeSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeSlotId),
            BuildingUpgradeTargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeTargetLevel),
            BuildingUpgradeMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.BuildingUpgradeMaxAttempts, 30),
            BuildingConstructSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructSlotId),
            BuildingConstructGid = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructGid),
            BuildingConstructName = configuration[BotOptionPayloadKeys.BuildingConstructName] ?? string.Empty,
            ConstructFasterEnabled = configuration.GetValue(BotOptionPayloadKeys.ConstructFasterEnabled, false),
            ConstructFasterMinBuildMinutes = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes, 30)),
            ConstructFasterRandomEnabled = configuration.GetValue(BotOptionPayloadKeys.ConstructFasterRandomEnabled, false),
            ConstructFasterRandomChancePercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ConstructFasterRandomChancePercent, 50), 0, 100),
            TargetBuildingSlotOrName = configuration[BotOptionPayloadKeys.TargetBuildingSlotOrName] ?? string.Empty,
            TargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.TargetLevel),
            HeroMinHpForAdventure = configuration.GetValue(BotOptionPayloadKeys.HeroMinHpForAdventure, 30),
            HeroHpRegenPerDayPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroHpRegenPerDayPercent, 40), 20, 100),
            HeroAutoRevive = configuration.GetValue(BotOptionPayloadKeys.HeroAutoRevive, false),
            HeroAutoAssignPoints = configuration.GetValue(BotOptionPayloadKeys.HeroAutoAssignPoints, false),
            HeroAutoUseOintments = configuration.GetValue(BotOptionPayloadKeys.HeroAutoUseOintments, false),
            HeroStatPriority = heroStatPriority,
            HeroAdventurePickOrder = configuration[BotOptionPayloadKeys.HeroAdventurePickOrder] ?? "shortest",
            HeroContinuousAdventures = configuration.GetValue(BotOptionPayloadKeys.HeroContinuousAdventures, false),
            IncreaseAdventuresToHard = configuration.GetValue(BotOptionPayloadKeys.IncreaseAdventuresToHard, false),
            ReduceAdventureTime = configuration.GetValue(BotOptionPayloadKeys.ReduceAdventureTime, false),
            AutoCollectTasksEnabled = configuration.GetValue(BotOptionPayloadKeys.AutoCollectTasksEnabled, true),
            AutoCollectDailyQuestsEnabled = configuration.GetValue(BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled, true),
            CollectStepDelayMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMinSeconds, PacingDefaults.CollectStepDelayMinSeconds)),
            CollectStepDelayMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMaxSeconds, PacingDefaults.CollectStepDelayMaxSeconds)),
            HeroResourceTransferEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroResourceTransferEnabled, true),
            HeroResourceMaxUseEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUseEnabled, true),
            HeroResourceMaxUsePerResource = configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUsePerResource, 5000),
            HeroResourceUseConstruction = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseConstruction, true),
            HeroResourceUseSmithy = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseSmithy, false),
            HeroResourceUseBrewery = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseBrewery, false),
            HeroResourceUseTownHall = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseTownHall, false),
            UpgradeSelectorProfile = configuration[BotOptionPayloadKeys.UpgradeSelectorProfile] ?? "auto",
        };
    }

    public static BotOptions CloneWithOverrides(
        BotOptions source,
        int? resourceUpgradeTargetLevelOverride = null,
        string? targetVillageNameOverride = null,
        string? targetVillageUrlOverride = null)
    {
        return new BotOptions
        {
            ServerName = source.ServerName,
            BaseUrl = source.BaseUrl,
            TimeoutMs = source.TimeoutMs,
            ManualLoginTimeoutSeconds = source.ManualLoginTimeoutSeconds,
            LoopIntervalSeconds = source.LoopIntervalSeconds,
            LoopTasks = source.LoopTasks,
            ContinuousLoopGroups = source.ContinuousLoopGroups,
            ContinuousFarmListNames = source.ContinuousFarmListNames,
            ContinuousFarmListIds = source.ContinuousFarmListIds,
            ContinuousFarmDispatchDelayMinMinutes = source.ContinuousFarmDispatchDelayMinMinutes,
            ContinuousFarmDispatchDelayMaxMinutes = source.ContinuousFarmDispatchDelayMaxMinutes,
            ContinuousFarmSendMode = source.ContinuousFarmSendMode,
            TownHallCelebrationMode = source.TownHallCelebrationMode,
            ContinuousFarmDeactivateLosses = source.ContinuousFarmDeactivateLosses,
            ContinuousFarmDeactivateOasisLosses = source.ContinuousFarmDeactivateOasisLosses,
            ContinuousFarmNextListIndex = source.ContinuousFarmNextListIndex,
            PostLoginAnalyzeFarmlists = source.PostLoginAnalyzeFarmlists,
            PostLoginAnalyzeHero = source.PostLoginAnalyzeHero,
            PostLoginAnalyzeHeroInventory = source.PostLoginAnalyzeHeroInventory,
            PostLoginReadTroopTrainingQueue = source.PostLoginReadTroopTrainingQueue,
            PostLoginAnalyzeBrewery = source.PostLoginAnalyzeBrewery,
            PostLoginAnalyzeNewVillages = source.PostLoginAnalyzeNewVillages,
            TroopTrainingBarracksEnabled = source.TroopTrainingBarracksEnabled,
            TroopTrainingBarracksTroopType = source.TroopTrainingBarracksTroopType,
            TroopTrainingBarracksMaxQueueHours = source.TroopTrainingBarracksMaxQueueHours,
            TroopTrainingBarracksAmountMode = source.TroopTrainingBarracksAmountMode,
            TroopTrainingBarracksKeepResourcesPercent = source.TroopTrainingBarracksKeepResourcesPercent,
            TroopTrainingBarracksRunMode = source.TroopTrainingBarracksRunMode,
            TroopTrainingBarracksMinimumTroops = source.TroopTrainingBarracksMinimumTroops,
            TroopTrainingBarracksMinimumResourcesPercent = source.TroopTrainingBarracksMinimumResourcesPercent,
            TroopTrainingBarracksTimedMinMinutes = source.TroopTrainingBarracksTimedMinMinutes,
            TroopTrainingBarracksTimedMaxMinutes = source.TroopTrainingBarracksTimedMaxMinutes,
            TroopTrainingBarracksCheckWood = source.TroopTrainingBarracksCheckWood,
            TroopTrainingBarracksCheckClay = source.TroopTrainingBarracksCheckClay,
            TroopTrainingBarracksCheckIron = source.TroopTrainingBarracksCheckIron,
            TroopTrainingBarracksCheckCrop = source.TroopTrainingBarracksCheckCrop,
            TroopTrainingStableEnabled = source.TroopTrainingStableEnabled,
            TroopTrainingStableTroopType = source.TroopTrainingStableTroopType,
            TroopTrainingStableMaxQueueHours = source.TroopTrainingStableMaxQueueHours,
            TroopTrainingStableAmountMode = source.TroopTrainingStableAmountMode,
            TroopTrainingStableKeepResourcesPercent = source.TroopTrainingStableKeepResourcesPercent,
            TroopTrainingStableRunMode = source.TroopTrainingStableRunMode,
            TroopTrainingStableMinimumTroops = source.TroopTrainingStableMinimumTroops,
            TroopTrainingStableMinimumResourcesPercent = source.TroopTrainingStableMinimumResourcesPercent,
            TroopTrainingStableTimedMinMinutes = source.TroopTrainingStableTimedMinMinutes,
            TroopTrainingStableTimedMaxMinutes = source.TroopTrainingStableTimedMaxMinutes,
            TroopTrainingStableCheckWood = source.TroopTrainingStableCheckWood,
            TroopTrainingStableCheckClay = source.TroopTrainingStableCheckClay,
            TroopTrainingStableCheckIron = source.TroopTrainingStableCheckIron,
            TroopTrainingStableCheckCrop = source.TroopTrainingStableCheckCrop,
            TroopTrainingWorkshopEnabled = source.TroopTrainingWorkshopEnabled,
            TroopTrainingWorkshopTroopType = source.TroopTrainingWorkshopTroopType,
            TroopTrainingWorkshopMaxQueueHours = source.TroopTrainingWorkshopMaxQueueHours,
            TroopTrainingWorkshopAmountMode = source.TroopTrainingWorkshopAmountMode,
            TroopTrainingWorkshopKeepResourcesPercent = source.TroopTrainingWorkshopKeepResourcesPercent,
            TroopTrainingWorkshopRunMode = source.TroopTrainingWorkshopRunMode,
            TroopTrainingWorkshopMinimumTroops = source.TroopTrainingWorkshopMinimumTroops,
            TroopTrainingWorkshopMinimumResourcesPercent = source.TroopTrainingWorkshopMinimumResourcesPercent,
            TroopTrainingWorkshopTimedMinMinutes = source.TroopTrainingWorkshopTimedMinMinutes,
            TroopTrainingWorkshopTimedMaxMinutes = source.TroopTrainingWorkshopTimedMaxMinutes,
            TroopTrainingWorkshopCheckWood = source.TroopTrainingWorkshopCheckWood,
            TroopTrainingWorkshopCheckClay = source.TroopTrainingWorkshopCheckClay,
            TroopTrainingWorkshopCheckIron = source.TroopTrainingWorkshopCheckIron,
            TroopTrainingWorkshopCheckCrop = source.TroopTrainingWorkshopCheckCrop,
            TroopTrainingFallbackCooldownSeconds = source.TroopTrainingFallbackCooldownSeconds,
            BreweryAutoCelebrationEnabled = source.BreweryAutoCelebrationEnabled,
            NpcTradeEnabled = source.NpcTradeEnabled,
            NpcTradeConstructionEnabled = source.NpcTradeConstructionEnabled,
            NpcTradeThresholdPercent = source.NpcTradeThresholdPercent,
            NpcTradeAnalyzeWood = source.NpcTradeAnalyzeWood,
            NpcTradeAnalyzeClay = source.NpcTradeAnalyzeClay,
            NpcTradeAnalyzeIron = source.NpcTradeAnalyzeIron,
            NpcTradeAnalyzeCrop = source.NpcTradeAnalyzeCrop,
            ResourceTransferEnabled = source.ResourceTransferEnabled,
            ResourceTransferTargetVillageName = source.ResourceTransferTargetVillageName,
            ResourceTransferSourceVillageNames = source.ResourceTransferSourceVillageNames,
            ResourceTransferSourceThresholdPercent = source.ResourceTransferSourceThresholdPercent,
            ResourceTransferSourceKeepPercent = source.ResourceTransferSourceKeepPercent,
            ResourceTransferTargetFillPercent = source.ResourceTransferTargetFillPercent,
            ResourceTransferSendWood = source.ResourceTransferSendWood,
            ResourceTransferSendClay = source.ResourceTransferSendClay,
            ResourceTransferSendIron = source.ResourceTransferSendIron,
            ResourceTransferSendCrop = source.ResourceTransferSendCrop,
            ReinforcementsEnabled = source.ReinforcementsEnabled,
            ReinforcementsTargetVillageName = source.ReinforcementsTargetVillageName,
            ReinforcementsSourceVillageNames = source.ReinforcementsSourceVillageNames,
            ReinforcementsTroopRules = source.ReinforcementsTroopRules,
            ReinforcementsSendMinMinutes = source.ReinforcementsSendMinMinutes,
            ReinforcementsSendMaxMinutes = source.ReinforcementsSendMaxMinutes,
            GithubReleasesUrl = source.GithubReleasesUrl,
            HumanLikeEnabled = source.HumanLikeEnabled,
            HumanLikeSpeed = source.HumanLikeSpeed,
            ActionPacingEnabled = source.ActionPacingEnabled,
            ActionPacingTaskMinSeconds = source.ActionPacingTaskMinSeconds,
            ActionPacingTaskMaxSeconds = source.ActionPacingTaskMaxSeconds,
            ActionPacingPageLoadMinSeconds = source.ActionPacingPageLoadMinSeconds,
            ActionPacingPageLoadMaxSeconds = source.ActionPacingPageLoadMaxSeconds,
            ActionPacingClickMinSeconds = source.ActionPacingClickMinSeconds,
            ActionPacingClickMaxSeconds = source.ActionPacingClickMaxSeconds,
            ActionPacingLoopMinSeconds = source.ActionPacingLoopMinSeconds,
            ActionPacingLoopMaxSeconds = source.ActionPacingLoopMaxSeconds,
            FarmListStepDelayMinSeconds = source.FarmListStepDelayMinSeconds,
            FarmListStepDelayMaxSeconds = source.FarmListStepDelayMaxSeconds,
            TargetVillageName = targetVillageNameOverride ?? source.TargetVillageName,
            TargetVillageUrl = targetVillageUrlOverride ?? source.TargetVillageUrl,
            AllowGoldSpending = source.AllowGoldSpending,
            AllowSilverSpending = source.AllowSilverSpending,
            GoldLimit = source.GoldLimit,
            SilverLimit = source.SilverLimit,
            ResourceUpgradeSlotId = source.ResourceUpgradeSlotId,
            ResourceUpgradeTargetLevel = resourceUpgradeTargetLevelOverride ?? source.ResourceUpgradeTargetLevel,
            ResourceUpgradeMaxAttempts = source.ResourceUpgradeMaxAttempts,
            ResourceBuildStrategy = source.ResourceBuildStrategy,
            SmithyUpgradeTargets = source.SmithyUpgradeTargets,
            BuildingUpgradeSlotId = source.BuildingUpgradeSlotId,
            BuildingUpgradeTargetLevel = source.BuildingUpgradeTargetLevel,
            BuildingUpgradeMaxAttempts = source.BuildingUpgradeMaxAttempts,
            BuildingConstructSlotId = source.BuildingConstructSlotId,
            BuildingConstructGid = source.BuildingConstructGid,
            BuildingConstructName = source.BuildingConstructName,
            ConstructFasterEnabled = source.ConstructFasterEnabled,
            ConstructFasterMinBuildMinutes = source.ConstructFasterMinBuildMinutes,
            ConstructFasterRandomEnabled = source.ConstructFasterRandomEnabled,
            ConstructFasterRandomChancePercent = source.ConstructFasterRandomChancePercent,
            TargetBuildingSlotOrName = source.TargetBuildingSlotOrName,
            TargetLevel = source.TargetLevel,
            HeroMinHpForAdventure = source.HeroMinHpForAdventure,
            HeroHpRegenPerDayPercent = source.HeroHpRegenPerDayPercent,
            HeroAutoRevive = source.HeroAutoRevive,
            HeroAutoAssignPoints = source.HeroAutoAssignPoints,
            HeroAutoUseOintments = source.HeroAutoUseOintments,
            HeroStatPriority = source.HeroStatPriority,
            HeroAdventurePickOrder = source.HeroAdventurePickOrder,
            HeroContinuousAdventures = source.HeroContinuousAdventures,
            IncreaseAdventuresToHard = source.IncreaseAdventuresToHard,
            ReduceAdventureTime = source.ReduceAdventureTime,
            HeroResourceTransferEnabled = source.HeroResourceTransferEnabled,
            HeroResourceMaxUseEnabled = source.HeroResourceMaxUseEnabled,
            HeroResourceMaxUsePerResource = source.HeroResourceMaxUsePerResource,
            HeroResourceUseConstruction = source.HeroResourceUseConstruction,
            HeroResourceUseSmithy = source.HeroResourceUseSmithy,
            HeroResourceUseBrewery = source.HeroResourceUseBrewery,
            HeroResourceUseTownHall = source.HeroResourceUseTownHall,
            AutoCollectTasksEnabled = source.AutoCollectTasksEnabled,
            AutoCollectDailyQuestsEnabled = source.AutoCollectDailyQuestsEnabled,
            CollectStepDelayMinSeconds = source.CollectStepDelayMinSeconds,
            CollectStepDelayMaxSeconds = source.CollectStepDelayMaxSeconds,
            UpgradeSelectorProfile = source.UpgradeSelectorProfile,
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

    private static bool ResolveActionPacingEnabled(IConfiguration configuration, bool legacyHumanLikeEnabled)
    {
        if (configuration[BotOptionPayloadKeys.ActionPacingEnabled] is not null)
        {
            return configuration.GetValue(BotOptionPayloadKeys.ActionPacingEnabled, PacingDefaults.ActionPacingEnabled);
        }

        return legacyHumanLikeEnabled || PacingDefaults.ActionPacingEnabled;
    }

    private static ActionPacingFallbacks ResolveActionPacingFallbacks(bool legacyHumanLikeEnabled, string? legacyHumanLikeSpeed)
    {
        if (!legacyHumanLikeEnabled)
        {
            return ActionPacingFallbacks.Default;
        }

        var (legacyMin, legacyMax) = legacyHumanLikeSpeed?.Trim().ToLowerInvariant() switch
        {
            "slow" => (2.5, 5.0),
            "fast" => (0.3, 1.0),
            _ => (1.0, 2.5),
        };

        return new ActionPacingFallbacks(
            Math.Max(PacingDefaults.ActionPacingTaskMinSeconds, legacyMin),
            Math.Max(PacingDefaults.ActionPacingTaskMaxSeconds, legacyMax),
            Math.Max(PacingDefaults.ActionPacingPageLoadMinSeconds, legacyMin),
            Math.Max(PacingDefaults.ActionPacingPageLoadMaxSeconds, legacyMax),
            Math.Max(PacingDefaults.ActionPacingClickMinSeconds, legacyMin),
            Math.Max(PacingDefaults.ActionPacingClickMaxSeconds, legacyMax),
            PacingDefaults.ActionPacingLoopMinSeconds,
            PacingDefaults.ActionPacingLoopMaxSeconds,
            Math.Max(PacingDefaults.FarmListStepDelayMinSeconds, legacyMin),
            Math.Max(PacingDefaults.FarmListStepDelayMaxSeconds, legacyMax));
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
        => string.Equals(value, "resource_percent", StringComparison.OrdinalIgnoreCase)
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

    private sealed record ActionPacingFallbacks(
        double TaskMinSeconds,
        double TaskMaxSeconds,
        double PageLoadMinSeconds,
        double PageLoadMaxSeconds,
        double ClickMinSeconds,
        double ClickMaxSeconds,
        double LoopMinSeconds,
        double LoopMaxSeconds,
        double FarmListMinSeconds,
        double FarmListMaxSeconds)
    {
        public static ActionPacingFallbacks Default { get; } = new(
            PacingDefaults.ActionPacingTaskMinSeconds,
            PacingDefaults.ActionPacingTaskMaxSeconds,
            PacingDefaults.ActionPacingPageLoadMinSeconds,
            PacingDefaults.ActionPacingPageLoadMaxSeconds,
            PacingDefaults.ActionPacingClickMinSeconds,
            PacingDefaults.ActionPacingClickMaxSeconds,
            PacingDefaults.ActionPacingLoopMinSeconds,
            PacingDefaults.ActionPacingLoopMaxSeconds,
            PacingDefaults.FarmListStepDelayMinSeconds,
            PacingDefaults.FarmListStepDelayMaxSeconds);
    }
}
