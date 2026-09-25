using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

public static class BotOptionsFactory
{
    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var tasks = configuration.GetSection("loop_tasks").Get<List<string>>() ?? ["status"];
        var continuousLoopGroups = configuration.GetSection("continuous_loop_groups").Get<List<string>>() ?? [];
        var baseUrl = (configuration["base_url"] ?? string.Empty).TrimEnd('/');
        var actionPacing = ActionPacingOptionsModule.FromConfiguration(configuration);
        var construction = ConstructionOptionsModule.FromConfiguration(configuration);
        var hero = HeroOptionsModule.FromConfiguration(configuration);
        var farming = FarmingOptionsModule.FromConfiguration(configuration);
        var postLogin = PostLoginOptionsModule.FromConfiguration(configuration);
        var troopTraining = TroopTrainingOptionsModule.FromConfiguration(configuration);
        var npcTrade = NpcTradeOptionsModule.FromConfiguration(configuration);
        var resourceTransfer = ResourceTransferOptionsModule.FromConfiguration(configuration);
        var reinforcements = ReinforcementOptionsModule.FromConfiguration(configuration);

        var options = new BotOptions
        {
            ServerName = configuration["server_name"] ?? string.Empty,
            BaseUrl = baseUrl,
            TimeoutMs = configuration.GetValue("timeout_ms", 20000),
            ManualLoginTimeoutSeconds = configuration.GetValue("manual_login_timeout_seconds", 180),
            LoopIntervalSeconds = configuration.GetValue("loop_interval_seconds", 60),
            LoopTasks = tasks,
            ContinuousLoopGroups = continuousLoopGroups,
            GithubReleasesUrl = configuration["github_releases_url"] ?? string.Empty,
            AllowGoldSpending = GetValueOrDefault(configuration, BotOptionPayloadKeys.AllowGoldSpending, defaultValue: false),
            AllowSilverSpending = configuration.GetValue("allow_silver_spending", false),
            GoldLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.GoldLimit, 100)),
            DailyGoldSpendingLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.DailyGoldSpendingLimit, 20)),
            SilverLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.SilverLimit, 100)),
            DailySilverSpendingLimit = Math.Max(0, configuration.GetValue(BotOptionPayloadKeys.DailySilverSpendingLimit, 10000)),
        };

        options = farming.ApplyTo(options);
        options = postLogin.ApplyTo(options);
        options = troopTraining.ApplyTo(options);
        options = npcTrade.ApplyTo(options);
        options = resourceTransfer.ApplyTo(options);
        options = reinforcements.ApplyTo(options);
        options = actionPacing.ApplyTo(options);
        options = hero.ApplyTo(options);
        return construction.ApplyTo(options);
    }

    public static BotOptions CloneWithOverrides(
        BotOptions source,
        int? resourceUpgradeTargetLevelOverride = null,
        string? targetVillageNameOverride = null,
        string? targetVillageUrlOverride = null)
    {
        // BotOptions is a record: `with` copies every property from source, so the
        // only fields we name are the three optional overrides. This makes it impossible
        // to silently drop a field when a new setting is added (the old hand-written copy
        // list did exactly that for NpcTradeBuildTimeLimit*).
        return source with
        {
            TargetVillageName = targetVillageNameOverride ?? source.TargetVillageName,
            TargetVillageUrl = targetVillageUrlOverride ?? source.TargetVillageUrl,
            ResourceUpgradeTargetLevel = resourceUpgradeTargetLevelOverride ?? source.ResourceUpgradeTargetLevel,
        };
    }

    private static bool GetValueOrDefault(IConfiguration configuration, string key, bool defaultValue)
    {
        return configuration[key] is null
            ? defaultValue
            : configuration.GetValue(key, defaultValue);
    }

}
