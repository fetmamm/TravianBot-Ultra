namespace TbotUltra.Core.Configuration;

public static class BotOptionPayloadKeys
{
    public const string TargetVillageName = "target_village_name";
    public const string TargetVillageUrl = "target_village_url";

    public const string ResourceUpgradeSlotId = "resource_upgrade_slot_id";
    public const string ResourceUpgradeTargetLevel = "resource_upgrade_target_level";
    public const string ResourceUpgradeMaxAttempts = "resource_upgrade_max_attempts";
    public const string ResourceUpgradeName = "resource_upgrade_name";
    public const string UpgradeRequiredWood = "upgrade_required_wood";
    public const string UpgradeRequiredClay = "upgrade_required_clay";
    public const string UpgradeRequiredIron = "upgrade_required_iron";
    public const string UpgradeRequiredCrop = "upgrade_required_crop";
    public const string UpgradeCurrentWood = "upgrade_current_wood";
    public const string UpgradeCurrentClay = "upgrade_current_clay";
    public const string UpgradeCurrentIron = "upgrade_current_iron";
    public const string UpgradeCurrentCrop = "upgrade_current_crop";
    public const string UpgradeProductionWood = "upgrade_production_wood";
    public const string UpgradeProductionClay = "upgrade_production_clay";
    public const string UpgradeProductionIron = "upgrade_production_iron";
    public const string UpgradeProductionCrop = "upgrade_production_crop";
    public const string UpgradeWaitSeconds = "upgrade_wait_seconds";
    public const string UpgradeWaitReason = "upgrade_wait_reason";
    public const string UpgradeBlockedLabel = "upgrade_blocked_label";

    public const string BuildingUpgradeSlotId = "building_upgrade_slot_id";
    public const string BuildingUpgradeTargetLevel = "building_upgrade_target_level";
    public const string BuildingUpgradeMaxAttempts = "building_upgrade_max_attempts";
    public const string BuildingUpgradeName = "building_upgrade_name";

    public const string BuildingConstructSlotId = "building_construct_slot_id";
    public const string BuildingConstructGid = "building_construct_gid";
    public const string BuildingConstructName = "building_construct_name";
    public const string TargetBuildingSlotOrName = "target_building_slot_or_name";
    public const string TargetLevel = "target_level";
    public const string HeroMinHpForAdventure = "hero_min_hp_for_adventure";
    public const string HeroAutoRevive = "hero_auto_revive";
    public const string HeroAutoAssignPoints = "hero_auto_assign_points";
    public const string HeroStatPriority = "hero_stat_priority";
    public const string HeroAdventurePickOrder = "hero_adventure_pick_order"; // "shortest" or "top"
    public const string HeroHideMode = "hero_hide_mode"; // "hide" or "fight"
    public const string HeroContinuousAdventures = "hero_continuous_adventures";
    public const string ContinuousFarmListNames = "continuous_farm_list_names";
    public const string ContinuousFarmDispatchDelayMinutes = "continuous_farm_dispatch_delay_minutes";
    public const string QueueWaitThresholdMode = "queue_wait_threshold_mode";
    public const string PostLoginAnalyzeFarmlists = "post_login_analyze_farmlists";
    public const string PostLoginAnalyzeHero = "post_login_analyze_hero";
    public const string PostLoginReadTroopTrainingQueue = "post_login_read_troop_training_queue";
    public const string PostLoginAnalyzeBrewery = "post_login_analyze_brewery";
    public const string TroopTrainingBarracksEnabled = "troop_training_barracks_enabled";
    public const string TroopTrainingBarracksTroopType = "troop_training_barracks_troop_type";
    public const string TroopTrainingBarracksMaxQueueHours = "troop_training_barracks_max_queue_hours";
    public const string TroopTrainingBarracksAmountMode = "troop_training_barracks_amount_mode";
    public const string TroopTrainingBarracksKeepResourcesPercent = "troop_training_barracks_keep_resources_percent";
    public const string TroopTrainingBarracksRunMode = "troop_training_barracks_run_mode";
    public const string TroopTrainingBarracksMinimumTroops = "troop_training_barracks_minimum_troops";
    public const string TroopTrainingBarracksMinimumResourcesPercent = "troop_training_barracks_minimum_resources_percent";
    public const string TroopTrainingBarracksCheckWood = "troop_training_barracks_check_wood";
    public const string TroopTrainingBarracksCheckClay = "troop_training_barracks_check_clay";
    public const string TroopTrainingBarracksCheckIron = "troop_training_barracks_check_iron";
    public const string TroopTrainingBarracksCheckCrop = "troop_training_barracks_check_crop";
    public const string TroopTrainingStableEnabled = "troop_training_stable_enabled";
    public const string TroopTrainingStableTroopType = "troop_training_stable_troop_type";
    public const string TroopTrainingStableMaxQueueHours = "troop_training_stable_max_queue_hours";
    public const string TroopTrainingStableAmountMode = "troop_training_stable_amount_mode";
    public const string TroopTrainingStableKeepResourcesPercent = "troop_training_stable_keep_resources_percent";
    public const string TroopTrainingStableRunMode = "troop_training_stable_run_mode";
    public const string TroopTrainingStableMinimumTroops = "troop_training_stable_minimum_troops";
    public const string TroopTrainingStableMinimumResourcesPercent = "troop_training_stable_minimum_resources_percent";
    public const string TroopTrainingStableCheckWood = "troop_training_stable_check_wood";
    public const string TroopTrainingStableCheckClay = "troop_training_stable_check_clay";
    public const string TroopTrainingStableCheckIron = "troop_training_stable_check_iron";
    public const string TroopTrainingStableCheckCrop = "troop_training_stable_check_crop";
    public const string TroopTrainingWorkshopEnabled = "troop_training_workshop_enabled";
    public const string TroopTrainingWorkshopTroopType = "troop_training_workshop_troop_type";
    public const string TroopTrainingWorkshopMaxQueueHours = "troop_training_workshop_max_queue_hours";
    public const string TroopTrainingWorkshopAmountMode = "troop_training_workshop_amount_mode";
    public const string TroopTrainingWorkshopKeepResourcesPercent = "troop_training_workshop_keep_resources_percent";
    public const string TroopTrainingWorkshopRunMode = "troop_training_workshop_run_mode";
    public const string TroopTrainingWorkshopMinimumTroops = "troop_training_workshop_minimum_troops";
    public const string TroopTrainingWorkshopMinimumResourcesPercent = "troop_training_workshop_minimum_resources_percent";
    public const string TroopTrainingWorkshopCheckWood = "troop_training_workshop_check_wood";
    public const string TroopTrainingWorkshopCheckClay = "troop_training_workshop_check_clay";
    public const string TroopTrainingWorkshopCheckIron = "troop_training_workshop_check_iron";
    public const string TroopTrainingWorkshopCheckCrop = "troop_training_workshop_check_crop";
    public const string TroopTrainingFallbackCooldownSeconds = "troop_training_fallback_cooldown_seconds";
    public const string BreweryAutoCelebrationEnabled = "brewery_auto_celebration_enabled";

    public const string UpgradeSelectorProfile = "upgrade_selector_profile";
    public const string CaptchaAutoSolveEnabled = "captcha_auto_solve_enabled";
    public const string CaptchaSolverTimeoutSeconds = "captcha_solver_timeout_seconds";
    public const string CaptchaSolverMaxAttempts = "captcha_solver_max_attempts";
}
