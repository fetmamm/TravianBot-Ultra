using Microsoft.Extensions.Configuration;
using static TbotUltra.Core.Configuration.PayloadValueReader;

namespace TbotUltra.Core.Configuration;

internal sealed record HeroOptions(
    int MinHpForAdventure,
    bool AutoRevive,
    bool AutoAssignPoints,
    bool AutoUseOintments,
    int OintmentTargetHpPercent,
    string StatPriority,
    string StatMaximums,
    string AdventurePickOrder,
    bool ContinuousAdventures,
    bool IncreaseAdventuresToHard,
    bool ReduceAdventureTime,
    int AdventureVideoChancePercent,
    bool AutoCollectTasksEnabled,
    bool AutoCollectDailyQuestsEnabled,
    bool ProductionBonusVideoEnabled,
    double CollectStepDelayMinSeconds,
    double CollectStepDelayMaxSeconds,
    bool ResourceTransferEnabled,
    bool ResourceMaxUseEnabled,
    int ResourceMaxUsePerResource,
    bool ResourceUseConstruction,
    bool ResourceUseSmithy,
    bool ResourceUseBrewery,
    bool ResourceUseTownHall)
{
    public bool AdventureRestartDelayEnabled { get; init; }
    public double AdventureRestartDelayMinMinutes { get; init; }
    public double AdventureRestartDelayMaxMinutes { get; init; }
    public int HpRegenPerDayPercent { get; init; }
    public bool CropAntiStarveEnabled { get; init; }
    public int CropAntiStarveTriggerMinutes { get; init; }
    public int CropAntiStarveTargetMinutes { get; init; }
    public int CropAntiStarveMaxCropPerTransfer { get; init; }
    public int CropAntiStarveMinHeroCropRemaining { get; init; }
}

internal static class HeroOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.HeroMinHpForAdventure,
        BotOptionPayloadKeys.HeroHpRegenPerDayPercent,
        BotOptionPayloadKeys.HeroAutoRevive,
        BotOptionPayloadKeys.HeroAutoAssignPoints,
        BotOptionPayloadKeys.HeroAutoUseOintments,
        BotOptionPayloadKeys.HeroOintmentTargetHpPercent,
        BotOptionPayloadKeys.HeroStatPriority,
        BotOptionPayloadKeys.HeroStatMaximums,
        BotOptionPayloadKeys.HeroAdventurePickOrder,
        BotOptionPayloadKeys.HeroContinuousAdventures,
        BotOptionPayloadKeys.IncreaseAdventuresToHard,
        BotOptionPayloadKeys.ReduceAdventureTime,
        BotOptionPayloadKeys.HeroAdventureVideoChancePercent,
        BotOptionPayloadKeys.AutoCollectTasksEnabled,
        BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled,
        BotOptionPayloadKeys.ProductionBonusVideoEnabled,
        BotOptionPayloadKeys.CollectStepDelayMinSeconds,
        BotOptionPayloadKeys.CollectStepDelayMaxSeconds,
        BotOptionPayloadKeys.HeroResourceTransferEnabled,
        BotOptionPayloadKeys.HeroResourceMaxUseEnabled,
        BotOptionPayloadKeys.HeroResourceMaxUsePerResource,
        BotOptionPayloadKeys.HeroResourceUseConstruction,
        BotOptionPayloadKeys.HeroResourceUseSmithy,
        BotOptionPayloadKeys.HeroResourceUseBrewery,
        BotOptionPayloadKeys.HeroResourceUseTownHall,
    ];

    private const string DefaultStatPriority = "resources,fighting_strength,offence_bonus,defence_bonus";

    internal static HeroOptions FromConfiguration(IConfiguration configuration)
    {
        var statPriority = string.IsNullOrWhiteSpace(configuration[BotOptionPayloadKeys.HeroStatPriority])
            ? DefaultStatPriority
            : configuration[BotOptionPayloadKeys.HeroStatPriority]!;

        return new HeroOptions(
            configuration.GetValue(BotOptionPayloadKeys.HeroMinHpForAdventure, 50),
            configuration.GetValue(BotOptionPayloadKeys.HeroAutoRevive, false),
            configuration.GetValue(BotOptionPayloadKeys.HeroAutoAssignPoints, false),
            configuration.GetValue(BotOptionPayloadKeys.HeroAutoUseOintments, false),
            NormalizeOintmentTarget(configuration.GetValue(BotOptionPayloadKeys.HeroOintmentTargetHpPercent, 100)),
            statPriority,
            HeroAttributeMaximums.Serialize(HeroAttributeMaximums.Parse(configuration[BotOptionPayloadKeys.HeroStatMaximums])),
            configuration[BotOptionPayloadKeys.HeroAdventurePickOrder] ?? "shortest",
            configuration.GetValue(BotOptionPayloadKeys.HeroContinuousAdventures, false),
            configuration.GetValue(BotOptionPayloadKeys.IncreaseAdventuresToHard, true),
            configuration.GetValue(BotOptionPayloadKeys.ReduceAdventureTime, false),
            Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroAdventureVideoChancePercent, 70), 0, 100),
            configuration.GetValue(BotOptionPayloadKeys.AutoCollectTasksEnabled, true),
            configuration.GetValue(BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled, true),
            configuration.GetValue(BotOptionPayloadKeys.ProductionBonusVideoEnabled, true),
            ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMinSeconds, PacingDefaults.CollectStepDelayMinSeconds)),
            ClampDelaySeconds(configuration.GetValue(BotOptionPayloadKeys.CollectStepDelayMaxSeconds, PacingDefaults.CollectStepDelayMaxSeconds)),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceTransferEnabled, true),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUseEnabled, true),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceMaxUsePerResource, 5000),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseConstruction, true),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseSmithy, false),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseBrewery, false),
            configuration.GetValue(BotOptionPayloadKeys.HeroResourceUseTownHall, false))
        {
            AdventureRestartDelayEnabled = configuration.GetValue(
                BotOptionPayloadKeys.HeroAdventureRestartDelayEnabled,
                HeroAdventureRestartDelayDefaults.Enabled),
            AdventureRestartDelayMinMinutes = configuration.GetValue(
                BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes,
                HeroAdventureRestartDelayDefaults.MinMinutes),
            AdventureRestartDelayMaxMinutes = configuration.GetValue(
                BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes,
                HeroAdventureRestartDelayDefaults.MaxMinutes),
            HpRegenPerDayPercent = Math.Clamp(configuration.GetValue(BotOptionPayloadKeys.HeroHpRegenPerDayPercent, 40), 20, 100),
            CropAntiStarveEnabled = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveEnabled, HeroCropAntiStarveDefaults.Enabled),
            CropAntiStarveTriggerMinutes = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveTriggerMinutes, HeroCropAntiStarveDefaults.TriggerMinutes),
            CropAntiStarveTargetMinutes = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveTargetMinutes, HeroCropAntiStarveDefaults.TargetMinutes),
            CropAntiStarveMaxCropPerTransfer = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveMaxCropPerTransfer, HeroCropAntiStarveDefaults.MaxCropPerTransfer),
            CropAntiStarveMinHeroCropRemaining = configuration.GetValue(BotOptionPayloadKeys.HeroCropAntiStarveMinHeroCropRemaining, HeroCropAntiStarveDefaults.MinHeroCropRemaining),
        };
    }

    internal static HeroOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new HeroOptions(
            source.HeroMinHpForAdventure,
            source.HeroAutoRevive,
            source.HeroAutoAssignPoints,
            source.HeroAutoUseOintments,
            source.HeroOintmentTargetHpPercent,
            source.HeroStatPriority,
            source.HeroStatMaximums,
            source.HeroAdventurePickOrder,
            source.HeroContinuousAdventures,
            source.IncreaseAdventuresToHard,
            source.ReduceAdventureTime,
            source.HeroAdventureVideoChancePercent,
            source.AutoCollectTasksEnabled,
            source.AutoCollectDailyQuestsEnabled,
            source.ProductionBonusVideoEnabled,
            source.CollectStepDelayMinSeconds,
            source.CollectStepDelayMaxSeconds,
            source.HeroResourceTransferEnabled,
            source.HeroResourceMaxUseEnabled,
            source.HeroResourceMaxUsePerResource,
            source.HeroResourceUseConstruction,
            source.HeroResourceUseSmithy,
            source.HeroResourceUseBrewery,
            source.HeroResourceUseTownHall)
        {
            AdventureRestartDelayEnabled = source.HeroAdventureRestartDelayEnabled,
            AdventureRestartDelayMinMinutes = source.HeroAdventureRestartDelayMinMinutes,
            AdventureRestartDelayMaxMinutes = source.HeroAdventureRestartDelayMaxMinutes,
            HpRegenPerDayPercent = source.HeroHpRegenPerDayPercent,
            CropAntiStarveEnabled = source.HeroCropAntiStarveEnabled,
            CropAntiStarveTriggerMinutes = source.HeroCropAntiStarveTriggerMinutes,
            CropAntiStarveTargetMinutes = source.HeroCropAntiStarveTargetMinutes,
            CropAntiStarveMaxCropPerTransfer = source.HeroCropAntiStarveMaxCropPerTransfer,
            CropAntiStarveMinHeroCropRemaining = source.HeroCropAntiStarveMinHeroCropRemaining,
        };

        if (payload is null)
        {
            return result;
        }

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;

            if (TryReadInt(key, value, BotOptionPayloadKeys.HeroMinHpForAdventure, out var minHp))
                result = result with { MinHpForAdventure = minHp };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroAutoRevive, out var autoRevive))
                result = result with { AutoRevive = autoRevive };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroAutoAssignPoints, out var autoAssign))
                result = result with { AutoAssignPoints = autoAssign };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroAutoUseOintments, out var ointments))
                result = result with { AutoUseOintments = ointments };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.HeroOintmentTargetHpPercent, out var ointmentTarget))
                result = result with { OintmentTargetHpPercent = NormalizeOintmentTarget(ointmentTarget) };
            else if (key.Equals(BotOptionPayloadKeys.HeroStatPriority, StringComparison.OrdinalIgnoreCase))
                result = result with { StatPriority = value };
            else if (key.Equals(BotOptionPayloadKeys.HeroStatMaximums, StringComparison.OrdinalIgnoreCase))
                result = result with { StatMaximums = HeroAttributeMaximums.Serialize(HeroAttributeMaximums.Parse(value)) };
            else if (key.Equals(BotOptionPayloadKeys.HeroAdventurePickOrder, StringComparison.OrdinalIgnoreCase))
                result = result with { AdventurePickOrder = value };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroContinuousAdventures, out var continuous))
                result = result with { ContinuousAdventures = continuous };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.IncreaseAdventuresToHard, out var increaseHard))
                result = result with { IncreaseAdventuresToHard = increaseHard };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ReduceAdventureTime, out var reduceTime))
                result = result with { ReduceAdventureTime = reduceTime };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.HeroAdventureVideoChancePercent, out var videoChance))
                result = result with { AdventureVideoChancePercent = Math.Clamp(videoChance, 0, 100) };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.AutoCollectTasksEnabled, out var collectTasks))
                result = result with { AutoCollectTasksEnabled = collectTasks };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled, out var collectQuests))
                result = result with { AutoCollectDailyQuestsEnabled = collectQuests };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ProductionBonusVideoEnabled, out var productionBonus))
                result = result with { ProductionBonusVideoEnabled = productionBonus };
            else if (TryReadDouble(key, value, BotOptionPayloadKeys.CollectStepDelayMinSeconds, out var collectMin))
                result = result with { CollectStepDelayMinSeconds = ClampDelaySeconds(collectMin) };
            else if (TryReadDouble(key, value, BotOptionPayloadKeys.CollectStepDelayMaxSeconds, out var collectMax))
                result = result with { CollectStepDelayMaxSeconds = ClampDelaySeconds(collectMax) };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroResourceTransferEnabled, out var transfer))
                result = result with { ResourceTransferEnabled = transfer };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroResourceMaxUseEnabled, out var maxUse))
                result = result with { ResourceMaxUseEnabled = maxUse };
            else if (TryReadInt(key, value, BotOptionPayloadKeys.HeroResourceMaxUsePerResource, out var maxAmount))
                result = result with { ResourceMaxUsePerResource = Math.Max(0, maxAmount) };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroResourceUseConstruction, out var construction))
                result = result with { ResourceUseConstruction = construction };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroResourceUseSmithy, out var smithy))
                result = result with { ResourceUseSmithy = smithy };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroResourceUseBrewery, out var brewery))
                result = result with { ResourceUseBrewery = brewery };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroResourceUseTownHall, out var townHall))
                result = result with { ResourceUseTownHall = townHall };
        }

        return result;
    }

    internal static BotOptions ApplyTo(this HeroOptions values, BotOptions source)
        => source with
        {
            HeroMinHpForAdventure = values.MinHpForAdventure,
            HeroAutoRevive = values.AutoRevive,
            HeroAutoAssignPoints = values.AutoAssignPoints,
            HeroAutoUseOintments = values.AutoUseOintments,
            HeroOintmentTargetHpPercent = values.OintmentTargetHpPercent,
            HeroStatPriority = values.StatPriority,
            HeroStatMaximums = values.StatMaximums,
            HeroAdventurePickOrder = values.AdventurePickOrder,
            HeroContinuousAdventures = values.ContinuousAdventures,
            IncreaseAdventuresToHard = values.IncreaseAdventuresToHard,
            ReduceAdventureTime = values.ReduceAdventureTime,
            HeroAdventureVideoChancePercent = values.AdventureVideoChancePercent,
            AutoCollectTasksEnabled = values.AutoCollectTasksEnabled,
            AutoCollectDailyQuestsEnabled = values.AutoCollectDailyQuestsEnabled,
            ProductionBonusVideoEnabled = values.ProductionBonusVideoEnabled,
            CollectStepDelayMinSeconds = values.CollectStepDelayMinSeconds,
            CollectStepDelayMaxSeconds = values.CollectStepDelayMaxSeconds,
            HeroResourceTransferEnabled = values.ResourceTransferEnabled,
            HeroResourceMaxUseEnabled = values.ResourceMaxUseEnabled,
            HeroResourceMaxUsePerResource = values.ResourceMaxUsePerResource,
            HeroResourceUseConstruction = values.ResourceUseConstruction,
            HeroResourceUseSmithy = values.ResourceUseSmithy,
            HeroResourceUseBrewery = values.ResourceUseBrewery,
            HeroResourceUseTownHall = values.ResourceUseTownHall,
            HeroAdventureRestartDelayEnabled = values.AdventureRestartDelayEnabled,
            HeroAdventureRestartDelayMinMinutes = values.AdventureRestartDelayMinMinutes,
            HeroAdventureRestartDelayMaxMinutes = values.AdventureRestartDelayMaxMinutes,
            HeroHpRegenPerDayPercent = values.HpRegenPerDayPercent,
            HeroCropAntiStarveEnabled = values.CropAntiStarveEnabled,
            HeroCropAntiStarveTriggerMinutes = values.CropAntiStarveTriggerMinutes,
            HeroCropAntiStarveTargetMinutes = values.CropAntiStarveTargetMinutes,
            HeroCropAntiStarveMaxCropPerTransfer = values.CropAntiStarveMaxCropPerTransfer,
            HeroCropAntiStarveMinHeroCropRemaining = values.CropAntiStarveMinHeroCropRemaining,
        };

    private static double ClampDelaySeconds(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 3600);

    private static int NormalizeOintmentTarget(int value)
        => value is 50 or 60 or 70 or 80 or 90 or 100 ? value : 100;

}
