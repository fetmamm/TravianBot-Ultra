using System.Globalization;

namespace TbotUltra.Core.Travian;

public enum ExpansionBuildingType
{
    Residence = 25,
    Palace = 26,
    CommandCenter = 44,
}

public sealed record SettlerTrainingPlan(
    int ExpansionSlots,
    int FreeSlots,
    int SettlersAvailable,
    int SettlersToTrain,
    string Reason);

public sealed record VillageFoundingState(
    int SettlersAtHome,
    int FreeExpansionSlots,
    long CulturePoints,
    long CulturePointsNeededForNextVillage,
    IReadOnlyDictionary<string, long> Resources,
    bool TargetFieldIsFree);

public sealed record VillageFoundingReadiness(bool IsReady, IReadOnlyList<string> MissingRequirements);

/// <summary>
/// Expansion rules for settlers and founding new villages. One expansion slot takes 3 settlers; the
/// settlers carry 750 of each resource to the new village.
/// </summary>
public static class ExpansionCalculator
{
    public const int SettlersPerVillage = 3;
    public const int FoundingResourcesEach = 750;

    private static readonly IReadOnlyDictionary<ExpansionBuildingType, int[]> SlotLevels =
        new Dictionary<ExpansionBuildingType, int[]>
        {
            [ExpansionBuildingType.Residence] = [10, 20],
            [ExpansionBuildingType.Palace] = [10, 15, 20],
            [ExpansionBuildingType.CommandCenter] = [10, 15, 20],
        };

    private static readonly string[] ResourceKeys = ["wood", "clay", "iron", "crop"];

    public static int ExpansionSlots(ExpansionBuildingType building, int level)
        => SlotLevels[building].Count(required => level >= required);

    /// <summary>The building level that unlocks the next expansion slot, or null when every slot is unlocked.</summary>
    public static int? NextSlotLevel(ExpansionBuildingType building, int level)
        => SlotLevels[building].Where(required => required > level).Select(required => (int?)required).FirstOrDefault();

    /// <summary>
    /// Reads "used/total" from the settlement-slot footer ("5/5"). Every non-digit is dropped, so locale
    /// separators and bidirectional markers do not matter.
    /// </summary>
    public static (int Used, int Total)? ParseSettlementSlots(string? text)
        => ParseFraction(text) is var (used, total) && total > 0 ? ((int)used, (int)total) : null;

    /// <summary>Reads "current / needed" culture points from the next-slot bar caption.</summary>
    public static (long Current, long Needed)? ParseCulturePointsProgress(string? caption)
        => ParseFraction(caption) is var (current, needed) && needed > 0 ? (current, needed) : null;

    /// <summary>Seconds until the culture points reach the next slot, or null when nothing is produced.</summary>
    public static int? SecondsUntilCulturePoints(long current, long needed, long perDay)
    {
        if (current >= needed)
        {
            return 0;
        }

        return perDay > 0 ? (int)Math.Ceiling((needed - current) * 86400d / perDay) : null;
    }

    /// <summary>Reads a whole number such as the daily culture-point total, ignoring separators and markers.</summary>
    public static long? ParseWholeNumber(string? text)
        => long.TryParse(new string((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static (long, long)? ParseFraction(string? text)
    {
        var parts = (text ?? string.Empty).Split('/');
        return parts.Length == 2 && ParseWholeNumber(parts[0]) is long left && ParseWholeNumber(parts[1]) is long right
            ? (left, right)
            : null;
    }

    /// <summary>
    /// Settlers still needed to fill every free expansion slot. Settlers at home, in training and on the way
    /// all count as available, so a run never trains more than the slots can use.
    /// </summary>
    public static SettlerTrainingPlan PlanSettlerTraining(
        ExpansionBuildingType building,
        int buildingLevel,
        int usedSlots,
        int settlersAtHome,
        int settlersInTraining,
        int settlersOnTheWay)
    {
        var slots = ExpansionSlots(building, buildingLevel);
        var freeSlots = Math.Max(0, slots - Math.Max(0, usedSlots));
        var available = Math.Max(0, settlersAtHome) + Math.Max(0, settlersInTraining) + Math.Max(0, settlersOnTheWay);
        if (slots == 0)
        {
            return new SettlerTrainingPlan(0, 0, available, 0, $"{building} level {buildingLevel} has no expansion slot (needs level {SlotLevels[building][0]}).");
        }

        if (freeSlots == 0)
        {
            return new SettlerTrainingPlan(slots, 0, available, 0, $"All {slots} expansion slot(s) of {building} are used.");
        }

        var toTrain = Math.Max(0, freeSlots * SettlersPerVillage - available);
        var reason = toTrain == 0
            ? $"{available} settler(s) already cover {freeSlots} free slot(s)."
            : $"{freeSlots} free slot(s) need {freeSlots * SettlersPerVillage} settler(s); {available} available.";
        return new SettlerTrainingPlan(slots, freeSlots, available, toTrain, reason);
    }

    /// <summary>
    /// Seconds until the stock covers the cost at the given hourly production, or null when a missing
    /// resource has no positive production.
    /// </summary>
    public static int? SecondsUntilAffordable(
        IReadOnlyDictionary<string, long> stock,
        IReadOnlyDictionary<string, double?> productionPerHour,
        IReadOnlyDictionary<string, long> cost)
    {
        var longest = 0;
        foreach (var key in ResourceKeys)
        {
            var missing = (cost.TryGetValue(key, out var needed) ? needed : 0) - (stock.TryGetValue(key, out var have) ? have : 0);
            if (missing <= 0)
            {
                continue;
            }

            if (!productionPerHour.TryGetValue(key, out var production) || production is not > 0)
            {
                return null;
            }

            longest = Math.Max(longest, (int)Math.Ceiling(missing / production.Value * 3600d));
        }

        return longest;
    }

    public static VillageFoundingReadiness EvaluateFounding(VillageFoundingState state)
    {
        var missing = new List<string>();
        if (state.SettlersAtHome < SettlersPerVillage)
        {
            missing.Add($"settlers at home {state.SettlersAtHome}/{SettlersPerVillage}");
        }

        if (state.FreeExpansionSlots <= 0)
        {
            missing.Add("no free expansion slot");
        }

        if (state.CulturePoints < state.CulturePointsNeededForNextVillage)
        {
            missing.Add($"culture points {state.CulturePoints}/{state.CulturePointsNeededForNextVillage}");
        }

        foreach (var key in ResourceKeys)
        {
            var amount = state.Resources.TryGetValue(key, out var value) ? value : 0;
            if (amount < FoundingResourcesEach)
            {
                missing.Add($"{key} {amount}/{FoundingResourcesEach}");
            }
        }

        if (!state.TargetFieldIsFree)
        {
            missing.Add("target field is not a free village field");
        }

        return new VillageFoundingReadiness(missing.Count == 0, missing);
    }
}
