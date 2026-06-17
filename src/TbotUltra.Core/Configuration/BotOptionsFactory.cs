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
        var continuousFarmDispatchDelayMinutes = FarmingDefaults.NormalizeDispatchDelayMinutes(
            configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes, FarmingDefaults.DefaultDispatchDelayMinutes));
        var continuousFarmDispatchDelayVariationPercent = FarmingDefaults.NormalizeDispatchDelayVariationPercent(
            configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDispatchDelayVariationPercent, FarmingDefaults.DefaultDispatchDelayVariationPercent));
        var continuousFarmSendMode = FarmingDefaults.NormalizeSendMode(configuration[BotOptionPayloadKeys.ContinuousFarmSendMode]);
        var queueWaitThresholdMode = configuration[BotOptionPayloadKeys.QueueWaitThresholdMode] ?? "smart";

        var baseUrl = (configuration["base_url"] ?? string.Empty).TrimEnd('/');
        var serverFlavor = ServerFlavorDetector.FromBaseUrl(baseUrl);
        var isOfficialServer = serverFlavor == ServerFlavor.Official;
        var heroStatPriority = string.IsNullOrWhiteSpace(configuration[BotOptionPayloadKeys.HeroStatPriority])
            ? DefaultHeroStatPriority
            : configuration[BotOptionPayloadKeys.HeroStatPriority]!;

        return new BotOptions
        {
            ServerName = configuration["server_name"] ?? string.Empty,
            BaseUrl = baseUrl,
            // ServerFlavor is a computed property derived from BaseUrl — no assignment needed.
            LoginPath = configuration["login_path"] ?? "/login.php",
            VillageOverviewPath = configuration["village_overview_path"] ?? "/dorf1.php",
            Headless = configuration.GetValue("headless", false),
            TimeoutMs = configuration.GetValue("timeout_ms", 20000),
            ManualLoginTimeoutSeconds = configuration.GetValue("manual_login_timeout_seconds", 180),
            CaptchaAutoSolveEnabled = configuration.GetValue(BotOptionPayloadKeys.CaptchaAutoSolveEnabled, false),
            CaptchaSolverTimeoutSeconds = configuration.GetValue(BotOptionPayloadKeys.CaptchaSolverTimeoutSeconds, 60),
            CaptchaSolverMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.CaptchaSolverMaxAttempts, 3),
            LoopIntervalSeconds = configuration.GetValue("loop_interval_seconds", 60),
            LoopTasks = tasks,
            ContinuousLoopGroups = continuousLoopGroups,
            ContinuousFarmListNames = continuousFarmListNames,
            ContinuousFarmListIds = continuousFarmListIds,
            ContinuousFarmDispatchDelayMinutes = continuousFarmDispatchDelayMinutes,
            ContinuousFarmDispatchDelayVariationPercent = continuousFarmDispatchDelayVariationPercent,
            ContinuousFarmSendMode = continuousFarmSendMode,
            ContinuousFarmDeactivateLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateLosses, false),
            ContinuousFarmDeactivateOasisLosses = configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses, false),
            QueueWaitThresholdMode = queueWaitThresholdMode,
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
            TroopTrainingBarracksRunMode = configuration[BotOptionPayloadKeys.TroopTrainingBarracksRunMode] ?? "resource_percent",
            TroopTrainingBarracksMinimumTroops = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops, 1)),
            TroopTrainingBarracksMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent, 50), 0, 100),
            TroopTrainingBarracksCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckWood, true),
            TroopTrainingBarracksCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckClay, true),
            TroopTrainingBarracksCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckIron, true),
            TroopTrainingBarracksCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop, true),
            TroopTrainingStableEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableEnabled, false),
            TroopTrainingStableTroopType = configuration[BotOptionPayloadKeys.TroopTrainingStableTroopType] ?? string.Empty,
            TroopTrainingStableMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours] ?? "no_limit",
            TroopTrainingStableAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingStableAmountMode] ?? "maximum",
            TroopTrainingStableKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent, 10), 0, 95),
            TroopTrainingStableRunMode = configuration[BotOptionPayloadKeys.TroopTrainingStableRunMode] ?? "resource_percent",
            TroopTrainingStableMinimumTroops = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumTroops, 1)),
            TroopTrainingStableMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent, 50), 0, 100),
            TroopTrainingStableCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckWood, true),
            TroopTrainingStableCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckClay, true),
            TroopTrainingStableCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckIron, true),
            TroopTrainingStableCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingStableCheckCrop, true),
            TroopTrainingWorkshopEnabled = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled, false),
            TroopTrainingWorkshopTroopType = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopTroopType] ?? string.Empty,
            TroopTrainingWorkshopMaxQueueHours = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours] ?? "no_limit",
            TroopTrainingWorkshopAmountMode = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode] ?? "maximum",
            TroopTrainingWorkshopKeepResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent, 10), 0, 95),
            TroopTrainingWorkshopRunMode = configuration[BotOptionPayloadKeys.TroopTrainingWorkshopRunMode] ?? "resource_percent",
            TroopTrainingWorkshopMinimumTroops = Math.Max(1, configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops, 1)),
            TroopTrainingWorkshopMinimumResourcesPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent, 50), 0, 100),
            TroopTrainingWorkshopCheckWood = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood, true),
            TroopTrainingWorkshopCheckClay = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay, true),
            TroopTrainingWorkshopCheckIron = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron, true),
            TroopTrainingWorkshopCheckCrop = configuration.GetValue(BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop, true),
            TroopTrainingFallbackCooldownSeconds = ClampTroopTrainingFallbackCooldownSeconds(configuration.GetValue(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds, 120)),
            NpcTradeEnabled = GetValueOrDefault(configuration, BotOptionPayloadKeys.NpcTradeEnabled, defaultValue: !isOfficialServer),
            NpcTradeConstructionEnabled = GetValueOrDefault(configuration, BotOptionPayloadKeys.NpcTradeConstructionEnabled, defaultValue: !isOfficialServer),
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
            GithubReleasesUrl = configuration["github_releases_url"] ?? string.Empty,
            HumanLikeEnabled = configuration.GetValue("human_like_enabled", false),
            HumanLikeSpeed = configuration["human_like_speed"] ?? "medium",
            ActionPacingEnabled = configuration.GetValue(BotOptionPayloadKeys.ActionPacingEnabled, PacingDefaults.ActionPacingEnabled),
            ActionPacingTaskMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingTaskMinSeconds, PacingDefaults.ActionPacingTaskMinSeconds)),
            ActionPacingTaskMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, PacingDefaults.ActionPacingTaskMaxSeconds)),
            ActionPacingPageLoadMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, PacingDefaults.ActionPacingPageLoadMinSeconds)),
            ActionPacingPageLoadMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, PacingDefaults.ActionPacingPageLoadMaxSeconds)),
            ActionPacingClickMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingClickMinSeconds, PacingDefaults.ActionPacingClickMinSeconds)),
            ActionPacingClickMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingClickMaxSeconds, PacingDefaults.ActionPacingClickMaxSeconds)),
            ActionPacingLoopMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingLoopMinSeconds, PacingDefaults.ActionPacingLoopMinSeconds)),
            ActionPacingLoopMaxSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, PacingDefaults.ActionPacingLoopMaxSeconds)),
            FarmListStepDelayMinSeconds = ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, PacingDefaults.FarmListStepDelayMinSeconds)),
            FarmListStepDelayMaxSeconds = Math.Max(
                ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, PacingDefaults.FarmListStepDelayMinSeconds)),
                ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, PacingDefaults.FarmListStepDelayMaxSeconds))),
            TargetVillageName = configuration[BotOptionPayloadKeys.TargetVillageName] ?? string.Empty,
            TargetVillageUrl = configuration[BotOptionPayloadKeys.TargetVillageUrl] ?? string.Empty,
            AllowGoldSpending = GetValueOrDefault(configuration, BotOptionPayloadKeys.AllowGoldSpending, defaultValue: false),
            AllowSilverSpending = configuration.GetValue("allow_silver_spending", false),
            GoldLimit = configuration.GetValue(BotOptionPayloadKeys.GoldLimit, isOfficialServer ? 300 : 800),
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
            TargetBuildingSlotOrName = configuration[BotOptionPayloadKeys.TargetBuildingSlotOrName] ?? string.Empty,
            TargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.TargetLevel),
            HeroMinHpForAdventure = configuration.GetValue(BotOptionPayloadKeys.HeroMinHpForAdventure, 30),
            HeroHpRegenPerDayPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroHpRegenPerDayPercent, 40), 20, 100),
            HeroAutoRevive = configuration.GetValue(BotOptionPayloadKeys.HeroAutoRevive, false),
            HeroAutoAssignPoints = configuration.GetValue(BotOptionPayloadKeys.HeroAutoAssignPoints, false),
            HeroAutoUseOintments = configuration.GetValue(BotOptionPayloadKeys.HeroAutoUseOintments, false),
            HeroStatPriority = heroStatPriority,
            HeroAdventurePickOrder = configuration[BotOptionPayloadKeys.HeroAdventurePickOrder] ?? "shortest",
            HeroHideModeEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroHideModeEnabled, false),
            HeroHideMode = configuration[BotOptionPayloadKeys.HeroHideMode] ?? "hide",
            HeroContinuousAdventures = configuration.GetValue(BotOptionPayloadKeys.HeroContinuousAdventures, false),
            AutoCollectTasksEnabled = configuration.GetValue(BotOptionPayloadKeys.AutoCollectTasksEnabled, true),
            AutoCollectDailyQuestsEnabled = configuration.GetValue(BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled, true),
            CollectStepDelayMinMs = configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMinMs, PacingDefaults.CollectStepDelayMinMs),
            CollectStepDelayMaxMs = configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMaxMs, PacingDefaults.CollectStepDelayMaxMs),
            HeroResourceTransferEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroResourceTransferEnabled, true),
            HeroResourceMaxUseEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUseEnabled, true),
            HeroResourceMaxUsePerResource = configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUsePerResource, 5000),
            HeroResourceUseConstruction = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseConstruction, true),
            HeroResourceUseSmithy = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseSmithy, true),
            HeroResourceUseBrewery = configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseBrewery, true),
            UpgradeSelectorProfile = configuration[BotOptionPayloadKeys.UpgradeSelectorProfile] ?? "auto",
            NatarVillageSelection = configuration["natar_village_selection"] ?? "farm_villages",
        };
    }

    public static BotOptions CloneWithOverrides(
        BotOptions source,
        bool? headlessOverride = null,
        int? resourceUpgradeTargetLevelOverride = null,
        string? natarVillageSelectionOverride = null,
        string? targetVillageNameOverride = null,
        string? targetVillageUrlOverride = null)
    {
        return new BotOptions
        {
            ServerName = source.ServerName,
            BaseUrl = source.BaseUrl,
            LoginPath = source.LoginPath,
            VillageOverviewPath = source.VillageOverviewPath,
            Headless = headlessOverride ?? source.Headless,
            TimeoutMs = source.TimeoutMs,
            ManualLoginTimeoutSeconds = source.ManualLoginTimeoutSeconds,
            CaptchaAutoSolveEnabled = source.CaptchaAutoSolveEnabled,
            CaptchaSolverTimeoutSeconds = source.CaptchaSolverTimeoutSeconds,
            CaptchaSolverMaxAttempts = source.CaptchaSolverMaxAttempts,
            LoopIntervalSeconds = source.LoopIntervalSeconds,
            LoopTasks = source.LoopTasks,
            ContinuousLoopGroups = source.ContinuousLoopGroups,
            ContinuousFarmListNames = source.ContinuousFarmListNames,
            ContinuousFarmListIds = source.ContinuousFarmListIds,
            ContinuousFarmDispatchDelayMinutes = source.ContinuousFarmDispatchDelayMinutes,
            ContinuousFarmDispatchDelayVariationPercent = source.ContinuousFarmDispatchDelayVariationPercent,
            ContinuousFarmSendMode = source.ContinuousFarmSendMode,
            ContinuousFarmDeactivateLosses = source.ContinuousFarmDeactivateLosses,
            ContinuousFarmDeactivateOasisLosses = source.ContinuousFarmDeactivateOasisLosses,
            ContinuousFarmNextListIndex = source.ContinuousFarmNextListIndex,
            QueueWaitThresholdMode = source.QueueWaitThresholdMode,
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
            TargetBuildingSlotOrName = source.TargetBuildingSlotOrName,
            TargetLevel = source.TargetLevel,
            HeroMinHpForAdventure = source.HeroMinHpForAdventure,
            HeroHpRegenPerDayPercent = source.HeroHpRegenPerDayPercent,
            HeroAutoRevive = source.HeroAutoRevive,
            HeroAutoAssignPoints = source.HeroAutoAssignPoints,
            HeroAutoUseOintments = source.HeroAutoUseOintments,
            HeroStatPriority = source.HeroStatPriority,
            HeroAdventurePickOrder = source.HeroAdventurePickOrder,
            HeroHideModeEnabled = source.HeroHideModeEnabled,
            HeroHideMode = source.HeroHideMode,
            HeroContinuousAdventures = source.HeroContinuousAdventures,
            HeroResourceTransferEnabled = source.HeroResourceTransferEnabled,
            HeroResourceMaxUseEnabled = source.HeroResourceMaxUseEnabled,
            HeroResourceMaxUsePerResource = source.HeroResourceMaxUsePerResource,
            HeroResourceUseConstruction = source.HeroResourceUseConstruction,
            HeroResourceUseSmithy = source.HeroResourceUseSmithy,
            HeroResourceUseBrewery = source.HeroResourceUseBrewery,
            AutoCollectTasksEnabled = source.AutoCollectTasksEnabled,
            AutoCollectDailyQuestsEnabled = source.AutoCollectDailyQuestsEnabled,
            CollectStepDelayMinMs = source.CollectStepDelayMinMs,
            CollectStepDelayMaxMs = source.CollectStepDelayMaxMs,
            UpgradeSelectorProfile = source.UpgradeSelectorProfile,
            NatarVillageSelection = string.IsNullOrWhiteSpace(natarVillageSelectionOverride)
                ? source.NatarVillageSelection
                : natarVillageSelectionOverride,
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
