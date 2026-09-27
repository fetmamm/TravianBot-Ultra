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
            GeneralSettingsOptionsModule.FromConfiguration(configuration),
            ActionPacingOptionsModule.ReadSettings(configuration));
    }

    public static JsonObject Apply(JsonObject source, SettingsConfiguration settings)
    {
        var target = (JsonObject)source.DeepClone();
        target.Remove("headless");
        target.Remove("queue_wait_threshold_mode");
        GeneralSettingsOptionsModule.WriteSettings(target, settings.General, settings.Options);
        ActionPacingOptionsModule.WriteSettings(target, settings.SessionPacing, settings.Options);
        ConstructionOptionsModule.WriteSettings(target, settings.Options);
        FarmingOptionsModule.WriteSettings(target, settings.Options);
        HeroOptionsModule.WriteSettings(target, settings.Options);
        PostLoginOptionsModule.WriteSettings(target, settings.Options);
        TroopTrainingOptionsModule.WriteSettings(target, settings.Options);
        return target;
    }

    internal static void WriteIntRange(JsonObject target, string minKey, string maxKey, int min, int max, int floor, int ceiling)
    {
        var normalizedMin = Math.Clamp(min, floor, ceiling);
        target[minKey] = normalizedMin;
        target[maxKey] = Math.Max(normalizedMin, Math.Clamp(max, floor, ceiling));
    }

    internal static void WriteDelayRange(JsonObject target, string minKey, string maxKey, double min, double max, double floor, double ceiling)
    {
        var normalizedMin = ClampFinite(min, floor, ceiling);
        target[minKey] = normalizedMin;
        target[maxKey] = Math.Max(normalizedMin, ClampFinite(max, floor, ceiling));
    }

    internal static double ClampFinite(double value, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}
