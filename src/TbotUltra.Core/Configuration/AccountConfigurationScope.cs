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
        .. SpendingOptionsModule.AccountScopedKeys,
        "loop_interval_seconds",
        "loop_tasks",
        "continuous_loop_groups",
        "continuous_loop_group_order",
        "dashboard_visible_groups",
    ];
}
