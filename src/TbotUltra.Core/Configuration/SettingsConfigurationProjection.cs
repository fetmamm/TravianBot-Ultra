using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace TbotUltra.Core.Configuration;

public sealed record GeneralSettingsConfiguration(
    bool DontNotifyNewVersion,
    bool QuickReloginEnabled,
    bool DailyServerResetOverrideEnabled,
    int DailyServerResetHour);

public sealed record SessionPacingSettingsConfiguration(
    bool SessionPacingEnabled,
    bool SmartSleepEnabled,
    bool SmartSleepWakeWhenConstructionQueueClears,
    int SmartSleepMinimumOpportunityMinutes,
    int SmartSleepWakeBeforeMinutes,
    int SmartSleepWakeAfterMinutes,
    int SmartSleepFallbackMinMinutes,
    int SmartSleepFallbackMaxMinutes,
    IReadOnlyList<string> SmartSleepDeadlineGroups,
    int SessionRunMinMinutes,
    int SessionRunMaxMinutes,
    int SessionSleepMinMinutes,
    int SessionSleepMaxMinutes,
    int SessionDailyMaxHours,
    int SessionDailyMaxVariationPercent,
    IReadOnlyList<int> SessionAllowedHours,
    int SessionHoursVariationPercent);

public sealed record SettingsConfiguration(
    BotOptions Options,
    GeneralSettingsConfiguration General,
    SessionPacingSettingsConfiguration SessionPacing);

public sealed record TroopTrainingPanelSettingsConfiguration(
    NpcTradeOptions NpcTrade,
    bool BreweryAutoCelebrationEnabled,
    bool AllowGoldSpending,
    int GoldLimit);

/// <summary>
/// Owns the Settings dialog's persisted configuration projection. WPF supplies typed edits and never
/// needs to know configuration keys, defaults, compatibility aliases, or normalization rules.
/// </summary>
public static class SettingsConfigurationProjection
{
    public static SettingsConfiguration FromConfiguration(IConfiguration configuration)
    {
        var options = BotOptionsFactory.FromConfiguration(configuration);
        return new SettingsConfiguration(
            options,
            GeneralSettingsOptionsModule.FromConfiguration(
                configuration,
                PostLoginOptionsModule.ReadQuickRelogin(configuration)),
            ActionPacingOptionsModule.ReadSettings(configuration));
    }

    public static JsonObject Apply(JsonObject source, SettingsConfiguration settings)
    {
        var target = (JsonObject)source.DeepClone();
        target.Remove("headless");
        target.Remove("queue_wait_threshold_mode");
        GeneralSettingsOptionsModule.WriteSettings(target, settings.General);
        SpendingOptionsModule.WriteSettings(target, SpendingOptionsModule.FromOptions(settings.Options));
        ActionPacingOptionsModule.WriteSettings(target, settings.SessionPacing, settings.Options);
        ConstructionOptionsModule.WriteSettings(target, settings.Options);
        FarmingOptionsModule.WriteSettings(target, settings.Options);
        HeroOptionsModule.WriteSettings(target, settings.Options);
        PostLoginOptionsModule.WriteSettings(target, settings.Options, settings.General.QuickReloginEnabled);
        TroopTrainingOptionsModule.WriteSettings(target, settings.Options);
        return target;
    }

    public static JsonObject ApplyTroopTrainingPanel(
        JsonObject source,
        TroopTrainingPanelSettingsConfiguration settings)
    {
        var target = (JsonObject)source.DeepClone();
        NpcTradeOptionsModule.WriteSettings(target, settings.NpcTrade);
        TroopTrainingOptionsModule.WriteBrewerySettings(target, settings.BreweryAutoCelebrationEnabled);
        SpendingOptionsModule.WriteGoldSettings(target, settings.AllowGoldSpending, settings.GoldLimit);
        return target;
    }

}
