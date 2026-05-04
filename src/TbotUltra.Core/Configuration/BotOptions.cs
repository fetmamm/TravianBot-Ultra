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

    [ConfigurationKeyName("login_path")]
    [Required]
    public string LoginPath { get; init; } = string.Empty;

    [ConfigurationKeyName("village_overview_path")]
    [Required]
    public string VillageOverviewPath { get; init; } = string.Empty;

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
    public int CaptchaSolverMaxAttempts { get; init; } = 1;

    [ConfigurationKeyName("loop_interval_seconds")]
    [Range(1, int.MaxValue)]
    public int LoopIntervalSeconds { get; init; } = 60;

    [ConfigurationKeyName("loop_tasks")]
    public List<string> LoopTasks { get; init; } = ["status"];

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
    public bool AllowGoldSpending { get; init; }

    [ConfigurationKeyName("allow_silver_spending")]
    public bool AllowSilverSpending { get; init; }

    [ConfigurationKeyName("gold_limit")]
    public int GoldLimit { get; init; } = 100;

    [ConfigurationKeyName("silver_limit")]
    public int SilverLimit { get; init; } = 100;

    [ConfigurationKeyName("resource_upgrade_slot_id")]
    public int? ResourceUpgradeSlotId { get; init; }

    [ConfigurationKeyName("resource_upgrade_target_level")]
    public int? ResourceUpgradeTargetLevel { get; init; }

    [ConfigurationKeyName("resource_upgrade_max_attempts")]
    public int ResourceUpgradeMaxAttempts { get; init; } = 30;

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

    [ConfigurationKeyName("hero_auto_revive")]
    public bool HeroAutoRevive { get; init; } = true;

    [ConfigurationKeyName("hero_auto_assign_points")]
    public bool HeroAutoAssignPoints { get; init; } = true;

    [ConfigurationKeyName("hero_stat_priority")]
    public string HeroStatPriority { get; init; } = "fighting_strength,offence_bonus,defence_bonus,resources";

    [ConfigurationKeyName("hero_adventure_pick_order")]
    public string HeroAdventurePickOrder { get; init; } = "shortest"; // "shortest" or "top"

    [ConfigurationKeyName("hero_hide_mode")]
    public string HeroHideMode { get; init; } = "hide"; // "hide" or "fight"

    [ConfigurationKeyName("upgrade_selector_profile")]
    public string UpgradeSelectorProfile { get; init; } = "auto";

    [ConfigurationKeyName("natar_village_selection")]
    public string NatarVillageSelection { get; init; } = "farm_villages";
}
