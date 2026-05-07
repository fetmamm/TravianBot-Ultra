using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

public static class BotOptionsFactory
{
    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var tasks = configuration.GetSection("loop_tasks").Get<List<string>>() ?? ["status"];
        var continuousLoopGroups = configuration.GetSection("continuous_loop_groups").Get<List<string>>() ?? [];
        var continuousFarmListNames = configuration.GetSection(BotOptionPayloadKeys.ContinuousFarmListNames).Get<List<string>>() ?? [];
        var continuousFarmDispatchDelayMinutes = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes, 1), 1, 5);
        var queueWaitThresholdMode = configuration[BotOptionPayloadKeys.QueueWaitThresholdMode] ?? "10";

        return new BotOptions
        {
            ServerName = configuration["server_name"] ?? string.Empty,
            BaseUrl = (configuration["base_url"] ?? string.Empty).TrimEnd('/'),
            LoginPath = configuration["login_path"] ?? "/login.php",
            VillageOverviewPath = configuration["village_overview_path"] ?? "/dorf1.php",
            Headless = configuration.GetValue("headless", false),
            TimeoutMs = configuration.GetValue("timeout_ms", 15000),
            ManualLoginTimeoutSeconds = configuration.GetValue("manual_login_timeout_seconds", 180),
            CaptchaAutoSolveEnabled = configuration.GetValue(BotOptionPayloadKeys.CaptchaAutoSolveEnabled, false),
            CaptchaSolverTimeoutSeconds = configuration.GetValue(BotOptionPayloadKeys.CaptchaSolverTimeoutSeconds, 60),
            CaptchaSolverMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.CaptchaSolverMaxAttempts, 1),
            LoopIntervalSeconds = configuration.GetValue("loop_interval_seconds", 60),
            LoopTasks = tasks,
            ContinuousLoopGroups = continuousLoopGroups,
            ContinuousFarmListNames = continuousFarmListNames,
            ContinuousFarmDispatchDelayMinutes = continuousFarmDispatchDelayMinutes,
            QueueWaitThresholdMode = queueWaitThresholdMode,
            PostLoginAnalyzeFarmlists = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists, false),
            PostLoginAnalyzeHero = configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHero, false),
            PostLoginReadTroopTrainingQueue = configuration.GetValue(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue, false),
            GithubReleasesUrl = configuration["github_releases_url"] ?? string.Empty,
            HumanLikeEnabled = configuration.GetValue("human_like_enabled", false),
            HumanLikeSpeed = configuration["human_like_speed"] ?? "medium",
            TargetVillageName = configuration[BotOptionPayloadKeys.TargetVillageName] ?? string.Empty,
            TargetVillageUrl = configuration[BotOptionPayloadKeys.TargetVillageUrl] ?? string.Empty,
            AllowGoldSpending = configuration.GetValue("allow_gold_spending", false),
            AllowSilverSpending = configuration.GetValue("allow_silver_spending", false),
            GoldLimit = configuration.GetValue("gold_limit", 100),
            SilverLimit = configuration.GetValue("silver_limit", 100),
            ResourceUpgradeSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeSlotId),
            ResourceUpgradeTargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.ResourceUpgradeTargetLevel),
            ResourceUpgradeMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.ResourceUpgradeMaxAttempts, 30),
            BuildingUpgradeSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeSlotId),
            BuildingUpgradeTargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingUpgradeTargetLevel),
            BuildingUpgradeMaxAttempts = configuration.GetValue(BotOptionPayloadKeys.BuildingUpgradeMaxAttempts, 30),
            BuildingConstructSlotId = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructSlotId),
            BuildingConstructGid = configuration.GetValue<int?>(BotOptionPayloadKeys.BuildingConstructGid),
            BuildingConstructName = configuration[BotOptionPayloadKeys.BuildingConstructName] ?? string.Empty,
            TargetBuildingSlotOrName = configuration[BotOptionPayloadKeys.TargetBuildingSlotOrName] ?? string.Empty,
            TargetLevel = configuration.GetValue<int?>(BotOptionPayloadKeys.TargetLevel),
            HeroMinHpForAdventure = configuration.GetValue(BotOptionPayloadKeys.HeroMinHpForAdventure, 60),
            HeroAutoRevive = configuration.GetValue(BotOptionPayloadKeys.HeroAutoRevive, true),
            HeroAutoAssignPoints = configuration.GetValue(BotOptionPayloadKeys.HeroAutoAssignPoints, true),
            HeroStatPriority = configuration[BotOptionPayloadKeys.HeroStatPriority] ?? "fighting_strength,offence_bonus,defence_bonus,resources",
            HeroAdventurePickOrder = configuration[BotOptionPayloadKeys.HeroAdventurePickOrder] ?? "shortest",
            HeroHideMode = configuration[BotOptionPayloadKeys.HeroHideMode] ?? "hide",
            UpgradeSelectorProfile = configuration[BotOptionPayloadKeys.UpgradeSelectorProfile] ?? "auto",
            NatarVillageSelection = configuration["natar_village_selection"] ?? "farm_villages",
        };
    }

    public static BotOptions CloneWithOverrides(
        BotOptions source,
        bool? headlessOverride = null,
        int? resourceUpgradeTargetLevelOverride = null,
        string? natarVillageSelectionOverride = null)
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
            ContinuousFarmDispatchDelayMinutes = source.ContinuousFarmDispatchDelayMinutes,
            QueueWaitThresholdMode = source.QueueWaitThresholdMode,
            PostLoginAnalyzeFarmlists = source.PostLoginAnalyzeFarmlists,
            PostLoginAnalyzeHero = source.PostLoginAnalyzeHero,
            PostLoginReadTroopTrainingQueue = source.PostLoginReadTroopTrainingQueue,
            GithubReleasesUrl = source.GithubReleasesUrl,
            HumanLikeEnabled = source.HumanLikeEnabled,
            HumanLikeSpeed = source.HumanLikeSpeed,
            TargetVillageName = source.TargetVillageName,
            TargetVillageUrl = source.TargetVillageUrl,
            AllowGoldSpending = source.AllowGoldSpending,
            AllowSilverSpending = source.AllowSilverSpending,
            GoldLimit = source.GoldLimit,
            SilverLimit = source.SilverLimit,
            ResourceUpgradeSlotId = source.ResourceUpgradeSlotId,
            ResourceUpgradeTargetLevel = resourceUpgradeTargetLevelOverride ?? source.ResourceUpgradeTargetLevel,
            ResourceUpgradeMaxAttempts = source.ResourceUpgradeMaxAttempts,
            BuildingUpgradeSlotId = source.BuildingUpgradeSlotId,
            BuildingUpgradeTargetLevel = source.BuildingUpgradeTargetLevel,
            BuildingUpgradeMaxAttempts = source.BuildingUpgradeMaxAttempts,
            BuildingConstructSlotId = source.BuildingConstructSlotId,
            BuildingConstructGid = source.BuildingConstructGid,
            BuildingConstructName = source.BuildingConstructName,
            TargetBuildingSlotOrName = source.TargetBuildingSlotOrName,
            TargetLevel = source.TargetLevel,
            HeroMinHpForAdventure = source.HeroMinHpForAdventure,
            HeroAutoRevive = source.HeroAutoRevive,
            HeroAutoAssignPoints = source.HeroAutoAssignPoints,
            HeroStatPriority = source.HeroStatPriority,
            HeroAdventurePickOrder = source.HeroAdventurePickOrder,
            HeroHideMode = source.HeroHideMode,
            UpgradeSelectorProfile = source.UpgradeSelectorProfile,
            NatarVillageSelection = string.IsNullOrWhiteSpace(natarVillageSelectionOverride)
                ? source.NatarVillageSelection
                : natarVillageSelectionOverride,
        };
    }
}
