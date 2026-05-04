namespace TbotUltra.Core.Configuration;

public static class BotOptionPayloadKeys
{
    public const string TargetVillageName = "target_village_name";
    public const string TargetVillageUrl = "target_village_url";

    public const string ResourceUpgradeSlotId = "resource_upgrade_slot_id";
    public const string ResourceUpgradeTargetLevel = "resource_upgrade_target_level";
    public const string ResourceUpgradeMaxAttempts = "resource_upgrade_max_attempts";

    public const string BuildingUpgradeSlotId = "building_upgrade_slot_id";
    public const string BuildingUpgradeTargetLevel = "building_upgrade_target_level";
    public const string BuildingUpgradeMaxAttempts = "building_upgrade_max_attempts";

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

    public const string UpgradeSelectorProfile = "upgrade_selector_profile";
    public const string CaptchaAutoSolveEnabled = "captcha_auto_solve_enabled";
    public const string CaptchaSolverTimeoutSeconds = "captcha_solver_timeout_seconds";
    public const string CaptchaSolverMaxAttempts = "captcha_solver_max_attempts";
}
