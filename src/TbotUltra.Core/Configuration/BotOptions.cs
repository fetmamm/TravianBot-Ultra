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

    /// <summary>
    /// Which kind of Travian server this is. Controls gating of private-server-only features
    /// (e.g. Natar farming). Always derived from the <see cref="BaseUrl"/> host — the
    /// authoritative signal (e.g. *.ss-travi.com => SsTravi). It is deliberately NOT bound from
    /// config, so a stale <c>server_flavor</c> value left over from a previous server can never
    /// mis-detect the flavor (regardless of how this BotOptions instance was constructed).
    /// </summary>
    public ServerFlavor ServerFlavor => ServerFlavorDetector.FromBaseUrl(BaseUrl);

    /// <summary>
    /// True when connected to the SS-Travi private server. Use this to gate
    /// private-server-only behaviour so it stays disabled on official servers.
    /// </summary>
    public bool IsPrivateServer => ServerFlavor == ServerFlavor.SsTravi;

    [ConfigurationKeyName("login_path")]
    [Required]
    public string LoginPath { get; init; } = "/login.php";

    [ConfigurationKeyName("village_overview_path")]
    [Required]
    public string VillageOverviewPath { get; init; } = "/dorf1.php";

    [ConfigurationKeyName("headless")]
    public bool Headless { get; init; }

    [ConfigurationKeyName("timeout_ms")]
    [Range(1000, int.MaxValue)]
    public int TimeoutMs { get; init; } = 15000;

    [ConfigurationKeyName("manual_login_timeout_seconds")]
    [Range(1, int.MaxValue)]
    public int ManualLoginTimeoutSeconds { get; init; } = 180;

    [ConfigurationKeyName(BotOptionPayloadKeys.CaptchaAutoSolveEnabled)]
    public bool CaptchaAutoSolveEnabled { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.CaptchaSolverTimeoutSeconds)]
    [Range(1, int.MaxValue)]
    public int CaptchaSolverTimeoutSeconds { get; init; } = 60;

    [ConfigurationKeyName(BotOptionPayloadKeys.CaptchaSolverMaxAttempts)]
    [Range(1, int.MaxValue)]
    public int CaptchaSolverMaxAttempts { get; init; } = 3;

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

    [ConfigurationKeyName(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes)]
    public int ContinuousFarmDispatchDelayMinutes { get; init; } = 3;

    [ConfigurationKeyName(BotOptionPayloadKeys.QueueWaitThresholdMode)]
    public string QueueWaitThresholdMode { get; init; } = "smart";

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists)]
    public bool PostLoginAnalyzeFarmlists { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeHero)]
    public bool PostLoginAnalyzeHero { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue)]
    public bool PostLoginReadTroopTrainingQueue { get; init; }

    [ConfigurationKeyName(BotOptionPayloadKeys.PostLoginAnalyzeBrewery)]
    public bool PostLoginAnalyzeBrewery { get; init; }

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
    public string TroopTrainingBarracksRunMode { get; init; } = "resource_percent";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops)]
    public int TroopTrainingBarracksMinimumTroops { get; init; } = 1;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent)]
    public int TroopTrainingBarracksMinimumResourcesPercent { get; init; } = 50;

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
    public string TroopTrainingStableRunMode { get; init; } = "resource_percent";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableMinimumTroops)]
    public int TroopTrainingStableMinimumTroops { get; init; } = 1;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent)]
    public int TroopTrainingStableMinimumResourcesPercent { get; init; } = 50;

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
    public string TroopTrainingWorkshopRunMode { get; init; } = "resource_percent";

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops)]
    public int TroopTrainingWorkshopMinimumTroops { get; init; } = 1;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent)]
    public int TroopTrainingWorkshopMinimumResourcesPercent { get; init; } = 50;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckWood)]
    public bool TroopTrainingWorkshopCheckWood { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckClay)]
    public bool TroopTrainingWorkshopCheckClay { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckIron)]
    public bool TroopTrainingWorkshopCheckIron { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingWorkshopCheckCrop)]
    public bool TroopTrainingWorkshopCheckCrop { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds)]
    public int TroopTrainingFallbackCooldownSeconds { get; init; } = 120;

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

    [ConfigurationKeyName("github_releases_url")]
    public string GithubReleasesUrl { get; init; } = string.Empty;

    [ConfigurationKeyName("human_like_enabled")]
    public bool HumanLikeEnabled { get; init; }

    [ConfigurationKeyName("human_like_speed")]
    public string HumanLikeSpeed { get; init; } = "medium";

    [ConfigurationKeyName(BotOptionPayloadKeys.TargetVillageName)]
    public string TargetVillageName { get; init; } = string.Empty;

    [ConfigurationKeyName(BotOptionPayloadKeys.TargetVillageUrl)]
    public string TargetVillageUrl { get; init; } = string.Empty;

    [ConfigurationKeyName("allow_gold_spending")]
    public bool AllowGoldSpending { get; init; } = true;

    [ConfigurationKeyName("allow_silver_spending")]
    public bool AllowSilverSpending { get; init; }

    [ConfigurationKeyName("gold_limit")]
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

    [ConfigurationKeyName("target_building_slot_or_name")]
    public string TargetBuildingSlotOrName { get; init; } = string.Empty;

    [ConfigurationKeyName("target_level")]
    public int? TargetLevel { get; init; }

    [ConfigurationKeyName("hero_min_hp_for_adventure")]
    public int HeroMinHpForAdventure { get; init; } = 60;

    /// <summary>How much hero HP regenerates per day, in percent (20–100). Used to compute how long
    /// to defer the hero group when HP is below the adventure threshold.</summary>
    [ConfigurationKeyName("hero_hp_regen_per_day_percent")]
    public int HeroHpRegenPerDayPercent { get; init; } = 100;

    [ConfigurationKeyName("hero_auto_revive")]
    public bool HeroAutoRevive { get; init; } = true;

    [ConfigurationKeyName("hero_auto_assign_points")]
    public bool HeroAutoAssignPoints { get; init; } = true;

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroAutoUseOintments)]
    public bool HeroAutoUseOintments { get; init; }

    [ConfigurationKeyName("hero_stat_priority")]
    public string HeroStatPriority { get; init; } = "fighting_strength,offence_bonus,defence_bonus,resources";

    [ConfigurationKeyName("hero_adventure_pick_order")]
    public string HeroAdventurePickOrder { get; init; } = "shortest"; // "shortest" or "top"

    [ConfigurationKeyName("hero_hide_mode")]
    public string HeroHideMode { get; init; } = "fight"; // "hide" or "fight"

    [ConfigurationKeyName(BotOptionPayloadKeys.HeroContinuousAdventures)]
    public bool HeroContinuousAdventures { get; init; }

    [ConfigurationKeyName("upgrade_selector_profile")]
    public string UpgradeSelectorProfile { get; init; } = "auto";

    [ConfigurationKeyName("natar_village_selection")]
    public string NatarVillageSelection { get; init; } = "farm_villages";
}
