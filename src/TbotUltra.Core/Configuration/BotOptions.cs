using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

public sealed class BotOptions
{
    [ConfigurationKeyName("server_name")]
    [Required]
    public string ServerName { get; init; } = string.Empty;

    [ConfigurationKeyName("base_url")]
    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    [ConfigurationKeyName("timeout_ms")]
    [Range(1000, int.MaxValue)]
    public int TimeoutMs { get; init; } = 15000;

    [ConfigurationKeyName("manual_login_timeout_seconds")]
    [Range(1, int.MaxValue)]
    public int ManualLoginTimeoutSeconds { get; init; } = 180;

    [ConfigurationKeyName("loop_interval_seconds")]
    [Range(1, int.MaxValue)]
    public int LoopIntervalSeconds { get; init; } = 60;

    [ConfigurationKeyName("loop_tasks")]
    public List<string> LoopTasks { get; init; } = ["status"];

    [ConfigurationKeyName("continuous_loop_groups")]
    public List<string> ContinuousLoopGroups { get; init; } = [];

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmListNames)]
    public List<string> ContinuousFarmListNames { get; init; } = [];

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmListIds)]
    public List<string> ContinuousFarmListIds { get; init; } = [];

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes)]
    public int ContinuousFarmDispatchDelayMinMinutes { get; init; } = FarmingDefaults.DefaultDispatchDelayMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes)]
    public int ContinuousFarmDispatchDelayMaxMinutes { get; init; } = FarmingDefaults.DefaultDispatchDelayMaxMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmSendMode)]
    public string ContinuousFarmSendMode { get; init; } = FarmingDefaults.SendModeListPerList;

    [ConfigurationKeyName(BotOptionPayloadKeys.TownHallCelebrationMode)]
    public string TownHallCelebrationMode { get; init; } = TownHallCelebrationDefaults.Small;

    [ConfigurationKeyName(BotOptionPayloadKeys.TownHallCelebrationCount)]
    public int TownHallCelebrationCount { get; init; } = TownHallCelebrationDefaults.DefaultCount;

    [ConfigurationKeyName(BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes)]
    public double TownHallCelebrationRestartDelayMinMinutes { get; init; } = TownHallCelebrationDefaults.DefaultRestartDelayMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes)]
    public double TownHallCelebrationRestartDelayMaxMinutes { get; init; } = TownHallCelebrationDefaults.DefaultRestartDelayMaxMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmDeactivateLosses)]
    public bool ContinuousFarmDeactivateLosses { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses)]
    public bool ContinuousFarmDeactivateOasisLosses { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmNextListIndex)]
    public int ContinuousFarmNextListIndex { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists)]
    public bool PostLoginAnalyzeFarmlists { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeHero)]
    public bool PostLoginAnalyzeHero { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory)]
    public bool PostLoginAnalyzeHeroInventory { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue)]
    public bool PostLoginReadTroopTrainingQueue { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeBrewery)]
    public bool PostLoginAnalyzeBrewery { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeNewVillages)]
    public bool PostLoginAnalyzeNewVillages { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.AutomaticallyCheckLanguage)]
    public bool AutomaticallyCheckLanguage { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.DetailedBrowserLoggingEnabled)]
    public bool DetailedBrowserLoggingEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksEnabled)]
    public bool TroopTrainingBarracksEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksTroopType)]
    public string TroopTrainingBarracksTroopType { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours)]
    public string TroopTrainingBarracksMaxQueueHours { get; init; } = "no_limit";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksAmountMode)]
    public string TroopTrainingBarracksAmountMode { get; init; } = "maximum";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent)]
    public int TroopTrainingBarracksKeepResourcesPercent { get; init; } = 10;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksRunMode)]
    public string TroopTrainingBarracksRunMode { get; init; } = "timed";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops)]
    public int TroopTrainingBarracksMinimumTroops { get; init; } = 1;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent)]
    public int TroopTrainingBarracksMinimumResourcesPercent { get; init; } = 50;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksTimedMinMinutes)]
    public int TroopTrainingBarracksTimedMinMinutes { get; init; } = 30;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksTimedMaxMinutes)]
    public int TroopTrainingBarracksTimedMaxMinutes { get; init; } = 120;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksCheckWood)]
    public bool TroopTrainingBarracksCheckWood { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksCheckClay)]
    public bool TroopTrainingBarracksCheckClay { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksCheckIron)]
    public bool TroopTrainingBarracksCheckIron { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop)]
    public bool TroopTrainingBarracksCheckCrop { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableEnabled)]
    public bool TroopTrainingStableEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableTroopType)]
    public string TroopTrainingStableTroopType { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours)]
    public string TroopTrainingStableMaxQueueHours { get; init; } = "no_limit";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableAmountMode)]
    public string TroopTrainingStableAmountMode { get; init; } = "maximum";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent)]
    public int TroopTrainingStableKeepResourcesPercent { get; init; } = 10;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableRunMode)]
    public string TroopTrainingStableRunMode { get; init; } = "timed";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableMinimumTroops)]
    public int TroopTrainingStableMinimumTroops { get; init; } = 1;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent)]
    public int TroopTrainingStableMinimumResourcesPercent { get; init; } = 50;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableTimedMinMinutes)]
    public int TroopTrainingStableTimedMinMinutes { get; init; } = 30;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableTimedMaxMinutes)]
    public int TroopTrainingStableTimedMaxMinutes { get; init; } = 120;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableCheckWood)]
    public bool TroopTrainingStableCheckWood { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableCheckClay)]
    public bool TroopTrainingStableCheckClay { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableCheckIron)]
    public bool TroopTrainingStableCheckIron { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableCheckCrop)]
    public bool TroopTrainingStableCheckCrop { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopEnabled)]
    public bool TroopTrainingWorkshopEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopTroopType)]
    public string TroopTrainingWorkshopTroopType { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours)]
    public string TroopTrainingWorkshopMaxQueueHours { get; init; } = "no_limit";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode)]
    public string TroopTrainingWorkshopAmountMode { get; init; } = "maximum";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent)]
    public int TroopTrainingWorkshopKeepResourcesPercent { get; init; } = 10;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopRunMode)]
    public string TroopTrainingWorkshopRunMode { get; init; } = "timed";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops)]
    public int TroopTrainingWorkshopMinimumTroops { get; init; } = 1;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent)]
    public int TroopTrainingWorkshopMinimumResourcesPercent { get; init; } = 50;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopTimedMinMinutes)]
    public int TroopTrainingWorkshopTimedMinMinutes { get; init; } = 30;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopTimedMaxMinutes)]
    public int TroopTrainingWorkshopTimedMaxMinutes { get; init; } = 120;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood)]
    public bool TroopTrainingWorkshopCheckWood { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay)]
    public bool TroopTrainingWorkshopCheckClay { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron)]
    public bool TroopTrainingWorkshopCheckIron { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop)]
    public bool TroopTrainingWorkshopCheckCrop { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds)]
    public int TroopTrainingFallbackCooldownSeconds { get; init; } = 300;

    [ConfigurationKeyName(BotOptionPayloadKeys.BreweryAutoCelebrationEnabled)]
    public bool BreweryAutoCelebrationEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeEnabled)]
    public bool NpcTradeEnabled { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeConstructionEnabled)]
    public bool NpcTradeConstructionEnabled { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeThresholdPercent)]
    public int NpcTradeThresholdPercent { get; init; } = 90;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeAnalyzeWood)]
    public bool NpcTradeAnalyzeWood { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeAnalyzeClay)]
    public bool NpcTradeAnalyzeClay { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeAnalyzeIron)]
    public bool NpcTradeAnalyzeIron { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeAnalyzeCrop)]
    public bool NpcTradeAnalyzeCrop { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeBuildTimeLimitEnabled)]
    public bool NpcTradeBuildTimeLimitEnabled { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.NpcTradeBuildTimeLimitSeconds)]
    public int NpcTradeBuildTimeLimitSeconds { get; init; } = 300;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferEnabled)]
    public bool ResourceTransferEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferTargetVillageName)]
    public string ResourceTransferTargetVillageName { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSourceVillageNames)]
    public List<string> ResourceTransferSourceVillageNames { get; init; } = [];

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent)]
    public int ResourceTransferSourceThresholdPercent { get; init; } = 50;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSourceKeepPercent)]
    public int ResourceTransferSourceKeepPercent { get; init; } = 5;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferTargetFillPercent)]
    public int ResourceTransferTargetFillPercent { get; init; } = 90;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSendWood)]
    public bool ResourceTransferSendWood { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSendClay)]
    public bool ResourceTransferSendClay { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSendIron)]
    public bool ResourceTransferSendIron { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.ResourceTransferSendCrop)]
    public bool ResourceTransferSendCrop { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.ReinforcementsEnabled)]
    public bool ReinforcementsEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ReinforcementsTargetVillageName)]
    public string ReinforcementsTargetVillageName { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.ReinforcementsSourceVillageNames)]
    public List<string> ReinforcementsSourceVillageNames { get; init; } = [];

    [ConfigurationKeyName(BotOptionPayloadKeys.ReinforcementsTroopRules)]
    public List<ReinforcementTroopRule> ReinforcementsTroopRules { get; init; } = [];

    [ConfigurationKeyName(BotOptionPayloadKeys.ReinforcementsSendMinMinutes)]
    public int ReinforcementsSendMinMinutes { get; init; } = ReinforcementSendDefaults.DefaultSendMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ReinforcementsSendMaxMinutes)]
    public int ReinforcementsSendMaxMinutes { get; init; } = ReinforcementSendDefaults.DefaultSendMaxMinutes;

    [ConfigurationKeyName("github_releases_url")]
    public string GithubReleasesUrl { get; init; } = string.Empty;

    [ConfigurationKeyName("human_like_enabled")]
    public bool HumanLikeEnabled { get; init; }

    [ConfigurationKeyName("human_like_speed")]
    public string HumanLikeSpeed { get; init; } = "medium";

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingEnabled)]
    public bool ActionPacingEnabled { get; init; } = PacingDefaults.ActionPacingEnabled;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingTaskMinSeconds)]
    public double ActionPacingTaskMinSeconds { get; init; } = PacingDefaults.ActionPacingTaskMinSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingTaskMaxSeconds)]
    public double ActionPacingTaskMaxSeconds { get; init; } = PacingDefaults.ActionPacingTaskMaxSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds)]
    public double ActionPacingPageLoadMinSeconds { get; init; } = PacingDefaults.ActionPacingPageLoadMinSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds)]
    public double ActionPacingPageLoadMaxSeconds { get; init; } = PacingDefaults.ActionPacingPageLoadMaxSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingClickMinSeconds)]
    public double ActionPacingClickMinSeconds { get; init; } = PacingDefaults.ActionPacingClickMinSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingClickMaxSeconds)]
    public double ActionPacingClickMaxSeconds { get; init; } = PacingDefaults.ActionPacingClickMaxSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingLoopMinSeconds)]
    public double ActionPacingLoopMinSeconds { get; init; } = PacingDefaults.ActionPacingLoopMinSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingLoopMaxSeconds)]
    public double ActionPacingLoopMaxSeconds { get; init; } = PacingDefaults.ActionPacingLoopMaxSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.FarmListStepDelayMinSeconds)]
    public double FarmListStepDelayMinSeconds { get; init; } = PacingDefaults.FarmListStepDelayMinSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.FarmListStepDelayMaxSeconds)]
    public double FarmListStepDelayMaxSeconds { get; init; } = PacingDefaults.FarmListStepDelayMaxSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBreakEnabled)]
    public bool ActionPacingIdleBreakEnabled { get; init; } = PacingDefaults.ActionPacingIdleBreakEnabled;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes)]
    public double ActionPacingIdleBreakIntervalMinMinutes { get; init; } = PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes)]
    public double ActionPacingIdleBreakIntervalMaxMinutes { get; init; } = PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes)]
    public double ActionPacingIdleBreakDurationMinMinutes { get; init; } = PacingDefaults.ActionPacingIdleBreakDurationMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes)]
    public double ActionPacingIdleBreakDurationMaxMinutes { get; init; } = PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled)]
    public bool ActionPacingIdleBrowseEnabled { get; init; } = PacingDefaults.ActionPacingIdleBrowseEnabled;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes)]
    public double ActionPacingIdleBrowseIntervalMinMinutes { get; init; } = PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes)]
    public double ActionPacingIdleBrowseIntervalMaxMinutes { get; init; } = PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap)]
    public bool ActionPacingIdleBrowsePageMap { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageMap;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics)]
    public bool ActionPacingIdleBrowsePageStatistics { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageStatistics;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero)]
    public bool ActionPacingIdleBrowsePageStatisticsHero { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10)]
    public bool ActionPacingIdleBrowsePageStatisticsTop10 { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders)]
    public bool ActionPacingIdleBrowsePageStatisticsDefenders { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers)]
    public bool ActionPacingIdleBrowsePageStatisticsAttackers { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports)]
    public bool ActionPacingIdleBrowsePageReports { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageReports;

    [ConfigurationKeyName(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages)]
    public bool ActionPacingIdleBrowsePageMessages { get; init; } = PacingDefaults.ActionPacingIdleBrowsePageMessages;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled)]
    public bool ConstructionHumanizeDelayEnabled { get; init; } = PacingDefaults.ConstructionHumanizeDelayEnabled;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead)]
    public int ConstructionStorageUpgradeLevelsAhead { get; init; } = ConstructionDefaults.StorageUpgradeLevelsAhead;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeStateVersion)]
    public int ConstructionHumanizeStateVersion { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin)]
    public double ConstructionHumanizeQueuePercentMin { get; init; } = PacingDefaults.ConstructionHumanizeQueuePercentMin;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax)]
    public double ConstructionHumanizeQueuePercentMax { get; init; } = PacingDefaults.ConstructionHumanizeQueuePercentMax;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes)]
    public double ConstructionHumanizeMaxDelayMinutes { get; init; } = PacingDefaults.ConstructionHumanizeMaxDelayMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes)]
    public double ConstructionHumanizeNoPlusMinMinutes { get; init; } = PacingDefaults.ConstructionHumanizeNoPlusMinMinutes;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes)]
    public double ConstructionHumanizeNoPlusMaxMinutes { get; init; } = PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes;

    // Per-queue-item flag set by the desktop's pre-sleep fill sweep; never stored in bot.json.
    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionPreSleepFill)]
    public bool ConstructionPreSleepFill { get; init; }

    // Per-queue-item flag set after login; never stored in bot.json.
    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionLoginFill)]
    public bool ConstructionLoginFill { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds)]
    public long? ConstructionLoginFillExpiresAtUnixSeconds { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.TargetVillageName)]
    public string TargetVillageName { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.TargetVillageUrl)]
    public string TargetVillageUrl { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.AllowGoldSpending)]
    public bool AllowGoldSpending { get; init; }

    [ConfigurationKeyName("allow_silver_spending")]
    public bool AllowSilverSpending { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.GoldLimit)]
    public int GoldLimit { get; init; } = 800;

    [ConfigurationKeyName("silver_limit")]
    public int SilverLimit { get; init; } = 100;

    [ConfigurationKeyName("resource_upgrade_slot_id")]
    public int? ResourceUpgradeSlotId { get; init; }

    [ConfigurationKeyName("resource_upgrade_target_level")]
    public int? ResourceUpgradeTargetLevel { get; init; }

    [ConfigurationKeyName("resource_upgrade_max_attempts")]
    public int ResourceUpgradeMaxAttempts { get; init; } = 30;

    [ConfigurationKeyName("resource_build_strategy")]
    public string ResourceBuildStrategy { get; init; } = "smart"; // "lowest_first" or "smart"

    // Smithy troop-upgrade targets, supplied only via task payload (not persisted in bot.json/settings.json;
    // the Desktop popup keeps them in config/accounts/<account>/smithy_upgrade.json). Compact form
    // "u21=20;u24=10". Null/empty => the smithy task is a no-op. Parsed by SmithyUpgradePayload.
    [ConfigurationKeyName("smithy_upgrade_targets")]
    public string? SmithyUpgradeTargets { get; init; }

    [ConfigurationKeyName("building_upgrade_slot_id")]
    public int? BuildingUpgradeSlotId { get; init; }

    [ConfigurationKeyName("building_upgrade_target_level")]
    public int? BuildingUpgradeTargetLevel { get; init; }

    [ConfigurationKeyName("building_upgrade_max_attempts")]
    public int BuildingUpgradeMaxAttempts { get; init; } = 30;

    [ConfigurationKeyName("building_construct_slot_id")]
    public int? BuildingConstructSlotId { get; init; }

    [ConfigurationKeyName("building_construct_gid")]
    public int? BuildingConstructGid { get; init; }

    [ConfigurationKeyName("building_construct_name")]
    public string BuildingConstructName { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.BuildingConstructAllowSlotFallback)]
    public bool BuildingConstructAllowSlotFallback { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots)]
    public string BuildingConstructFallbackExcludedSlots { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructFasterEnabled)]
    public bool ConstructFasterEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructFasterMinBuildTimeEnabled)]
    public bool ConstructFasterMinBuildTimeEnabled { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes)]
    public int ConstructFasterMinBuildMinutes { get; init; } = 30;

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructFasterRandomEnabled)]
    public bool ConstructFasterRandomEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.ConstructFasterRandomChancePercent)]
    public int ConstructFasterRandomChancePercent { get; init; } = 50;

    [ConfigurationKeyName("target_building_slot_or_name")]
    public string TargetBuildingSlotOrName { get; init; } = string.Empty;

    [ConfigurationKeyName("target_level")]
    public int? TargetLevel { get; init; }

    [ConfigurationKeyName("hero_min_hp_for_adventure")]
    public int HeroMinHpForAdventure { get; init; } = 50;

    /// <summary>How much hero HP regenerates per day, in percent (20–100). Used to compute how long
    /// to defer the hero group when HP is below the adventure threshold.</summary>
    [ConfigurationKeyName("hero_hp_regen_per_day_percent")]
    public int HeroHpRegenPerDayPercent { get; init; } = 40;

    [ConfigurationKeyName("hero_auto_revive")]
    public bool HeroAutoRevive { get; init; } = true;

    [ConfigurationKeyName("hero_auto_assign_points")]
    public bool HeroAutoAssignPoints { get; init; } = false;

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroAutoUseOintments)]
    public bool HeroAutoUseOintments { get; init; }

    [ConfigurationKeyName("hero_stat_priority")]
    public string HeroStatPriority { get; init; } = "resources,fighting_strength,offence_bonus,defence_bonus";

    [ConfigurationKeyName("hero_adventure_pick_order")]
    public string HeroAdventurePickOrder { get; init; } = "shortest"; // "shortest" or "top"

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroContinuousAdventures)]
    public bool HeroContinuousAdventures { get; init; }

    /// <summary>When true, the adventure function first activates "Increased adventure danger to hard"
    /// (via the bonus video) before dispatching the hero. Official Travian only.</summary>
    [ConfigurationKeyName(BotOptionPayloadKeys.IncreaseAdventuresToHard)]
    public bool IncreaseAdventuresToHard { get; init; }

    /// <summary>When true, the adventure function also activates "Reduce adventure duration by 25%"
    /// (via the bonus video) before dispatching the hero, after the increase-danger step. Official
    /// Travian only.</summary>
    [ConfigurationKeyName(BotOptionPayloadKeys.ReduceAdventureTime)]
    public bool ReduceAdventureTime { get; init; }

    /// <summary>Independent chance (0-100) to run each enabled hero-adventure bonus video.</summary>
    [ConfigurationKeyName(BotOptionPayloadKeys.HeroAdventureVideoChancePercent)]
    public int HeroAdventureVideoChancePercent { get; init; } = 70;

    [ConfigurationKeyName(BotOptionPayloadKeys.AutoCollectTasksEnabled)]
    public bool AutoCollectTasksEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled)]
    public bool AutoCollectDailyQuestsEnabled { get; init; }

    /// <summary>Account-wide: auto-activate the free +15% production bonus video on the Advantages
    /// tab for every resource that allows it after the daily 09:00 server-time reset. Official Travian only.</summary>
    [ConfigurationKeyName(BotOptionPayloadKeys.ProductionBonusVideoEnabled)]
    public bool ProductionBonusVideoEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.CollectStepDelayMinSeconds)]
    public double CollectStepDelayMinSeconds { get; init; } = PacingDefaults.CollectStepDelayMinSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.CollectStepDelayMaxSeconds)]
    public double CollectStepDelayMaxSeconds { get; init; } = PacingDefaults.CollectStepDelayMaxSeconds;

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceTransferEnabled)]
    public bool HeroResourceTransferEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceMaxUseEnabled)]
    public bool HeroResourceMaxUseEnabled { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceMaxUsePerResource)]
    public int HeroResourceMaxUsePerResource { get; init; } = 5000;

    // Per-consumer gates for the hero inventory top-up. Construction is the only default consumer.
    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceUseConstruction)]
    public bool HeroResourceUseConstruction { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceUseSmithy)]
    public bool HeroResourceUseSmithy { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceUseBrewery)]
    public bool HeroResourceUseBrewery { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroResourceUseTownHall)]
    public bool HeroResourceUseTownHall { get; init; }

    [ConfigurationKeyName("upgrade_selector_profile")]
    public string UpgradeSelectorProfile { get; init; } = "auto";

}
