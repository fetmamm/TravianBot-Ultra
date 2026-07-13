namespace TbotUltra.Core.Configuration;

internal sealed record HeroPayloadValues(
    int MinHpForAdventure,
    bool AutoRevive,
    bool AutoAssignPoints,
    bool AutoUseOintments,
    string StatPriority,
    string AdventurePickOrder,
    bool ContinuousAdventures,
    bool IncreaseAdventuresToHard,
    bool ReduceAdventureTime,
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
    bool ResourceUseTownHall);

internal static class HeroPayloadApplier
{
    internal static HeroPayloadValues Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new HeroPayloadValues(
            source.HeroMinHpForAdventure,
            source.HeroAutoRevive,
            source.HeroAutoAssignPoints,
            source.HeroAutoUseOintments,
            source.HeroStatPriority,
            source.HeroAdventurePickOrder,
            source.HeroContinuousAdventures,
            source.IncreaseAdventuresToHard,
            source.ReduceAdventureTime,
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
            source.HeroResourceUseTownHall);

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
            else if (key.Equals(BotOptionPayloadKeys.HeroStatPriority, StringComparison.OrdinalIgnoreCase))
                result = result with { StatPriority = value };
            else if (key.Equals(BotOptionPayloadKeys.HeroAdventurePickOrder, StringComparison.OrdinalIgnoreCase))
                result = result with { AdventurePickOrder = value };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.HeroContinuousAdventures, out var continuous))
                result = result with { ContinuousAdventures = continuous };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.IncreaseAdventuresToHard, out var increaseHard))
                result = result with { IncreaseAdventuresToHard = increaseHard };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.ReduceAdventureTime, out var reduceTime))
                result = result with { ReduceAdventureTime = reduceTime };
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

    private static double ClampDelaySeconds(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 3600);

    private static bool TryReadInt(string key, string value, string expected, out int parsed)
    {
        parsed = 0;
        return key.Equals(expected, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsed);
    }

    private static bool TryReadDouble(string key, string value, string expected, out double parsed)
    {
        parsed = 0;
        return key.Equals(expected, StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out parsed);
    }

    private static bool TryReadBool(string key, string value, string expected, out bool parsed)
    {
        parsed = false;
        return key.Equals(expected, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsed);
    }
}
