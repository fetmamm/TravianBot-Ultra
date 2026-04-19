using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Worker.Configuration;

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
}
