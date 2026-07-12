namespace TbotUltra.Core.Configuration;

public static class BotOptionPayloadKeys
{
    public const string TargetVillageName = "target_village_name";
    public const string TargetVillageUrl = "target_village_url";
    // Stable per-village identity (coordinate key, e.g. "xy:24|-65") stamped at enqueue time. Coordinates
    // survive renames AND lost-and-refounded villages that share a name, so queue gating/rotation never
    // resolve a queue item to the wrong village by name. Older items without this fall back to name/url.
    public const string TargetVillageKey = "target_village_key";

    public const string ResourceUpgradeSlotId = "resource_upgrade_slot_id";
    public const string ResourceUpgradeTargetLevel = "resource_upgrade_target_level";
    public const string ResourceBuildStrategy = "resource_build_strategy"; // "lowest_first" or "smart"
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
    public const string UpgradeWarehouseCapacity = "upgrade_warehouse_capacity";
    public const string UpgradeGranaryCapacity = "upgrade_granary_capacity";
    public const string UpgradeWaitSeconds = "upgrade_wait_seconds";
    public const string UpgradeWaitReason = "upgrade_wait_reason";
    public const string UpgradeBlockedLabel = "upgrade_blocked_label";
    public const string UpgradeStorageCapacityKind = "upgrade_storage_capacity_kind";
    // Why a construction item last deferred. Queue occupancy is kept separate from resource,
    // requirement and generic retry waits so scheduling and UI never infer a Travian queue from a timer.
    public const string UpgradeDeferReason = "upgrade_defer_reason";
    public const string UpgradeDeferClassificationVersion = "upgrade_defer_classification_version";
    public const string UpgradeDeferReasonQueueFull = "queue_full";
    public const string UpgradeDeferReasonInProgress = "in_progress";
    public const string UpgradeDeferReasonResources = "resources";
    public const string UpgradeDeferReasonRequirements = "requirements";
    public const string UpgradeDeferReasonStorageCapacity = "storage_capacity";
    public const string UpgradeDeferReasonRetry = "retry";
    // Consecutive requirement-defer counter. Requirement defers do not consume Retries (the prerequisite
    // could still arrive), so this bounds how long an item whose prerequisite never comes keeps retrying
    // before it is abandoned. Reset whenever the item defers for any other reason or makes progress.
    public const string RequirementDeferCount = "requirement_defer_count";
    public const string StorageDependencyParentId = "storage_dependency_parent_id";
    public const string StorageDependencyItemId = "storage_dependency_item_id";
    public const string StorageDependencyKind = "storage_dependency_kind";
    public const string AutoAddedBy = "auto_added_by";
    public const string AutoAddedReason = "auto_added_reason";
    public const string AutoAddedParentId = "auto_added_parent_id";
    public const string AutoAddedRequirement = "auto_added_requirement";
    public const string AutoAddedByConstructionRequirementRepair = "construction_requirement_repair";

    public const string BuildingUpgradeSlotId = "building_upgrade_slot_id";
    public const string BuildingUpgradeTargetLevel = "building_upgrade_target_level";
    public const string BuildingUpgradeMaxAttempts = "building_upgrade_max_attempts";
    public const string BuildingUpgradeName = "building_upgrade_name";

    // Compact list of smithy troop-upgrade targets, e.g. "u21=20;u24=10" (unit/troop key = target level).
    // Empty/absent means "no troops selected" and the task is a no-op. See SmithyUpgradePayload.
    public const string SmithyUpgradeTargets = "smithy_upgrade_targets";

    public const string BuildingConstructSlotId = "building_construct_slot_id";
    public const string BuildingConstructGid = "building_construct_gid";
    public const string BuildingConstructName = "building_construct_name";
    public const string ConstructFasterEnabled = "construct_faster_enabled";
    public const string ConstructFasterMinBuildTimeEnabled = "construct_faster_min_build_time_enabled";
    public const string ConstructFasterMinBuildMinutes = "construct_faster_min_build_minutes";
    public const string ConstructFasterRandomEnabled = "construct_faster_random_enabled";
    public const string ConstructFasterRandomChancePercent = "construct_faster_random_chance_percent";
    // Account-wide toggle: watch the free +15% production bonus video on the payment wizard's Advantages
    // tab for every resource that allows it after the daily server-time reset (reset hour is auto-detected
    // from the daily quests dialog per account, or overridden via DailyServerResetManualOverride below).
    // Never spends gold.
    public const string ProductionBonusVideoEnabled = "production_bonus_video_enabled";
    // Optional manual override for the daily server-reset hour (Settings > General). When the toggle is off
    // (default) the bot uses the hour auto-detected from the daily quests dialog; when on, this whole hour
    // (0..23, server-local) wins. Global (bot.json), like the other General settings.
    public const string DailyServerResetManualOverrideEnabled = "daily_server_reset_manual_override_enabled";
    public const string DailyServerResetManualHour = "daily_server_reset_manual_hour";
    public const string TargetBuildingSlotOrName = "target_building_slot_or_name";
    public const string TargetLevel = "target_level";
    public const string DontNotifyNewVersion = "dont_notify_new_version";
    public const string AutomaticallyCheckLanguage = "automatically_check_language";
    public const string HeroMinHpForAdventure = "hero_min_hp_for_adventure";
    public const string HeroHpRegenPerDayPercent = "hero_hp_regen_per_day_percent";
    public const string HeroAutoRevive = "hero_auto_revive";
    public const string HeroAutoAssignPoints = "hero_auto_assign_points";
    public const string HeroAutoUseOintments = "hero_auto_use_ointments";
    public const string HeroStatPriority = "hero_stat_priority";
    public const string HeroAdventurePickOrder = "hero_adventure_pick_order"; // "shortest" or "top"
    public const string HeroContinuousAdventures = "hero_continuous_adventures";
    public const string IncreaseAdventuresToHard = "increase_adventures_to_hard";
    public const string ReduceAdventureTime = "reduce_adventure_time";
    public const string AutoCollectTasksEnabled = "auto_collect_tasks_enabled";
    public const string AutoCollectDailyQuestsEnabled = "auto_collect_daily_quests_enabled";
    // Randomized delay (seconds) between internal clicks/steps in the auto-collect tasks/daily-quests
    // flows only. Min/max; set both to 0 to disable. Keeps these fast bursts from looking robotic.
    public const string CollectStepDelayMinSeconds = "collect_step_delay_min_seconds";
    public const string CollectStepDelayMaxSeconds = "collect_step_delay_max_seconds";
    public const string HeroResourceTransferEnabled = "hero_resource_transfer_enabled";
    // Caps how much may be pulled from the hero inventory per resource for a single construction
    // top-up. When the needed amount for any resource exceeds the limit, the transfer is skipped and
    // the build waits until the village has accumulated enough that the hero share fits the limit.
    public const string HeroResourceMaxUseEnabled = "hero_resource_max_use_enabled";
    public const string HeroResourceMaxUsePerResource = "hero_resource_max_use_per_resource";
    // Per-consumer gates for the hero inventory top-up (each default true). The master
    // HeroResourceTransferEnabled still applies on top of these.
    public const string HeroResourceUseConstruction = "hero_resource_use_construction";
    public const string HeroResourceUseSmithy = "hero_resource_use_smithy";
    public const string HeroResourceUseBrewery = "hero_resource_use_brewery";
    public const string HeroResourceUseTownHall = "hero_resource_use_town_hall";
    public const string ContinuousFarmListNames = "continuous_farm_list_names";
    // Stable Travian farm-list ids (lid) for the selected lists. Persisted alongside the names so
    // the selection survives a village/list rename: the name changes on Travian but the lid does not.
    public const string ContinuousFarmListIds = "continuous_farm_list_ids";
    public const string ContinuousFarmDispatchDelayMinMinutes = "continuous_farm_dispatch_delay_min_minutes";
    public const string ContinuousFarmDispatchDelayMaxMinutes = "continuous_farm_dispatch_delay_max_minutes";
    public const string ContinuousFarmSendMode = "continuous_farm_send_mode";
    public const string TownHallCelebrationMode = "town_hall_celebration_mode";
    public const string TownHallCelebrationCount = "town_hall_celebration_count";
    public const string TownHallCelebrationRestartDelayMinMinutes = "town_hall_celebration_restart_delay_min_minutes";
    public const string TownHallCelebrationRestartDelayMaxMinutes = "town_hall_celebration_restart_delay_max_minutes";
    public const string ContinuousFarmDeactivateLosses = "continuous_farm_deactivate_losses";
    public const string ContinuousFarmDeactivateOasisLosses = "continuous_farm_deactivate_oasis_losses";
    public const string ContinuousFarmNextListIndex = "continuous_farm_next_list_index";
    public const string PostLoginAnalyzeFarmlists = "post_login_analyze_farmlists";
    public const string PostLoginAnalyzeHero = "post_login_analyze_hero";
    public const string PostLoginAnalyzeHeroInventory = "post_login_analyze_hero_inventory";
    public const string PostLoginReadTroopTrainingQueue = "post_login_read_troop_training_queue";
    public const string PostLoginAnalyzeBrewery = "post_login_analyze_brewery";
    public const string PostLoginAnalyzeNewVillages = "post_login_analyze_new_villages";
    // Quick re-login: skip the post-login analyze stack when the last FULL login finished recently.
    public const string PostLoginQuickReloginEnabled = "post_login_quick_relogin_enabled";
    public const string PostLoginLastFullLoginAt = "post_login_last_full_login_at";
    public const string TroopTrainingBarracksEnabled = "troop_training_barracks_enabled";
    public const string TroopTrainingBarracksTroopType = "troop_training_barracks_troop_type";
    public const string TroopTrainingBarracksMaxQueueHours = "troop_training_barracks_max_queue_hours";
    public const string TroopTrainingBarracksAmountMode = "troop_training_barracks_amount_mode";
    public const string TroopTrainingBarracksKeepResourcesPercent = "troop_training_barracks_keep_resources_percent";
    public const string TroopTrainingBarracksRunMode = "troop_training_barracks_run_mode";
    public const string TroopTrainingBarracksMinimumTroops = "troop_training_barracks_minimum_troops";
    public const string TroopTrainingBarracksMinimumResourcesPercent = "troop_training_barracks_minimum_resources_percent";
    public const string TroopTrainingBarracksTimedMinMinutes = "troop_training_barracks_timed_min_minutes";
    public const string TroopTrainingBarracksTimedMaxMinutes = "troop_training_barracks_timed_max_minutes";
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
    public const string TroopTrainingStableTimedMinMinutes = "troop_training_stable_timed_min_minutes";
    public const string TroopTrainingStableTimedMaxMinutes = "troop_training_stable_timed_max_minutes";
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
    public const string TroopTrainingWorkshopTimedMinMinutes = "troop_training_workshop_timed_min_minutes";
    public const string TroopTrainingWorkshopTimedMaxMinutes = "troop_training_workshop_timed_max_minutes";
    public const string TroopTrainingWorkshopCheckWood = "troop_training_workshop_check_wood";
    public const string TroopTrainingWorkshopCheckClay = "troop_training_workshop_check_clay";
    public const string TroopTrainingWorkshopCheckIron = "troop_training_workshop_check_iron";
    public const string TroopTrainingWorkshopCheckCrop = "troop_training_workshop_check_crop";
    public const string TroopTrainingFallbackCooldownSeconds = "troop_training_fallback_cooldown_seconds";
    public const string BreweryAutoCelebrationEnabled = "brewery_auto_celebration_enabled";
    public const string NpcTradeEnabled = "npc_trade_enabled";
    public const string NpcTradeConstructionEnabled = "npc_trade_construction_enabled";
    public const string NpcTradeThresholdPercent = "npc_trade_threshold_percent";
    public const string NpcTradeAnalyzeWood = "npc_trade_analyze_wood";
    public const string NpcTradeAnalyzeClay = "npc_trade_analyze_clay";
    public const string NpcTradeAnalyzeIron = "npc_trade_analyze_iron";
    public const string NpcTradeAnalyzeCrop = "npc_trade_analyze_crop";
    public const string NpcTradeBuildTimeLimitEnabled = "npc_trade_build_time_limit_enabled";
    public const string NpcTradeBuildTimeLimitSeconds = "npc_trade_build_time_limit_seconds";
    public const string AllowGoldSpending = "allow_gold_spending";
    public const string GoldLimit = "gold_limit";

    public const string ResourceTransferEnabled = "resource_transfer_enabled";
    public const string ResourceTransferTargetVillageName = "resource_transfer_target_village_name";
    public const string ResourceTransferSourceVillageNames = "resource_transfer_source_village_names";
    public const string ResourceTransferSourceThresholdPercent = "resource_transfer_source_threshold_percent";
    public const string ResourceTransferSourceKeepPercent = "resource_transfer_source_keep_percent";
    public const string ResourceTransferTargetFillPercent = "resource_transfer_target_fill_percent";
    public const string ResourceTransferSendWood = "resource_transfer_send_wood";
    public const string ResourceTransferSendClay = "resource_transfer_send_clay";
    public const string ResourceTransferSendIron = "resource_transfer_send_iron";
    public const string ResourceTransferSendCrop = "resource_transfer_send_crop";

    public const string ReinforcementsEnabled = "reinforcements_enabled";
    public const string ReinforcementsTargetVillageName = "reinforcements_target_village_name";
    public const string ReinforcementsSourceVillageNames = "reinforcements_source_village_names";
    public const string ReinforcementsTroopRules = "reinforcements_troop_rules";
    public const string ReinforcementsSendMinMinutes = "reinforcements_send_min_minutes";
    public const string ReinforcementsSendMaxMinutes = "reinforcements_send_max_minutes";

    public const string UpgradeSelectorProfile = "upgrade_selector_profile";

    public const string SessionPacingEnabled = "session_pacing_enabled";
    public const string SessionPacingRunMinMinutes = "session_pacing_run_min_minutes";
    public const string SessionPacingRunMaxMinutes = "session_pacing_run_max_minutes";
    public const string SessionPacingSleepMinMinutes = "session_pacing_sleep_min_minutes";
    public const string SessionPacingSleepMaxMinutes = "session_pacing_sleep_max_minutes";
    public const string SessionPacingAllowedHours = "session_pacing_allowed_hours";
    public const string SessionPacingHoursVariationPercent = "session_pacing_hours_variation_percent";
    public const string SessionPacingDailyMaxHours = "session_pacing_daily_max_hours";
    public const string SessionPacingDailyMaxVariationPercent = "session_pacing_daily_max_variation_percent";
    public const string SessionPacingRuntimeDate = "session_pacing_runtime_date";
    public const string SessionPacingRuntimeSeconds = "session_pacing_runtime_seconds";
    public const string SessionPacingDailyHistory = "session_pacing_daily_history";
    public const string SessionActivityHistory = "session_activity_history";

    public const string ActionPacingEnabled = "action_pacing_enabled";
    public const string ActionPacingTaskMinSeconds = "action_pacing_task_min_seconds";
    public const string ActionPacingTaskMaxSeconds = "action_pacing_task_max_seconds";
    public const string ActionPacingPageLoadMinSeconds = "action_pacing_pageload_min_seconds";
    public const string ActionPacingPageLoadMaxSeconds = "action_pacing_pageload_max_seconds";
    public const string ActionPacingClickMinSeconds = "action_pacing_click_min_seconds";
    public const string ActionPacingClickMaxSeconds = "action_pacing_click_max_seconds";
    public const string ActionPacingLoopMinSeconds = "action_pacing_loop_min_seconds";
    public const string ActionPacingLoopMaxSeconds = "action_pacing_loop_max_seconds";
    public const string FarmListStepDelayMinSeconds = "farm_list_step_delay_min_seconds";
    public const string FarmListStepDelayMaxSeconds = "farm_list_step_delay_max_seconds";

    public const string ActionPacingIdleBreakEnabled = "action_pacing_idle_break_enabled";
    public const string ActionPacingIdleBreakIntervalMinMinutes = "action_pacing_idle_break_interval_min_minutes";
    public const string ActionPacingIdleBreakIntervalMaxMinutes = "action_pacing_idle_break_interval_max_minutes";
    public const string ActionPacingIdleBreakDurationMinMinutes = "action_pacing_idle_break_duration_min_minutes";
    public const string ActionPacingIdleBreakDurationMaxMinutes = "action_pacing_idle_break_duration_max_minutes";

    public const string ActionPacingIdleBrowseEnabled = "action_pacing_idle_browse_enabled";
    public const string ActionPacingIdleBrowseIntervalMinMinutes = "action_pacing_idle_browse_interval_min_minutes";
    public const string ActionPacingIdleBrowseIntervalMaxMinutes = "action_pacing_idle_browse_interval_max_minutes";
    public const string ActionPacingIdleBrowsePageMap = "action_pacing_idle_browse_page_map";
    public const string ActionPacingIdleBrowsePageStatistics = "action_pacing_idle_browse_page_statistics";
    public const string ActionPacingIdleBrowsePageReports = "action_pacing_idle_browse_page_reports";
    public const string ActionPacingIdleBrowsePageMessages = "action_pacing_idle_browse_page_messages";

    public const string ConstructionHumanizeDelayEnabled = "construction_humanize_delay_enabled";
    public const string ConstructionHumanizeQueuePercentMin = "construction_humanize_queue_percent_min";
    public const string ConstructionHumanizeQueuePercentMax = "construction_humanize_queue_percent_max";
    public const string ConstructionHumanizeMaxDelayMinutes = "construction_humanize_max_delay_minutes";
    public const string ConstructionHumanizeNoPlusMinMinutes = "construction_humanize_no_plus_min_minutes";
    public const string ConstructionHumanizeNoPlusMaxMinutes = "construction_humanize_no_plus_max_minutes";
}
