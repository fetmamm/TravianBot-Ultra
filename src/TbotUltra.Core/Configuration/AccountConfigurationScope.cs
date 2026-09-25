namespace TbotUltra.Core.Configuration;

public static class AccountConfigurationScope
{
    public static IReadOnlyList<string> AllKeys { get; } =
    [
        .. ConstructionOptionsModule.AccountScopedKeys,
        .. HeroOptionsModule.AccountScopedKeys,
        .. FarmingOptionsModule.AccountScopedKeys,
        .. PostLoginOptionsModule.AccountScopedKeys,
        .. TroopTrainingOptionsModule.AccountScopedKeys,
        .. NpcTradeOptionsModule.AccountScopedKeys,
        .. ResourceTransferOptionsModule.AccountScopedKeys,
        .. ReinforcementOptionsModule.AccountScopedKeys,
        .. ActionPacingOptionsModule.AccountScopedKeys,
        BotOptionPayloadKeys.AllowGoldSpending,
        BotOptionPayloadKeys.GoldLimit,
        BotOptionPayloadKeys.DailyGoldSpendingLimit,
        BotOptionPayloadKeys.SilverLimit,
        BotOptionPayloadKeys.DailySilverSpendingLimit,
        "loop_interval_seconds",
        "allow_silver_spending",
        "loop_tasks",
        "continuous_loop_groups",
        "continuous_loop_group_order",
        "dashboard_visible_groups",
    ];
}
