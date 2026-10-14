namespace TbotUltra.Core.Travian;

public sealed record HeroOasisRaidEstimate(
    int PredictedHpAfterRaid,
    int PredictedHpLoss,
    int EstimatedBountyPerResource,
    double AttackerLossFraction,
    double DefenderLossFraction);

/// <summary>
/// Approximate Official Travian raid combat for one hero against an unoccupied oasis.
/// This is not a survival guarantee: the caller must use live hero strength, HP, gear,
/// and a fresh garrison, then verify the actual outcome before another raid.
/// Formula: https://blog.travian.com/ru/2023/09/game-secrets-combat-system-formulas-written-by-kirilloid/
/// </summary>
public static class HeroOasisRaidEstimator
{
    public static bool TryEstimate(
        IReadOnlyList<NatureTroopGroup> garrison,
        int heroAttackStrength,
        int currentHpPercent,
        bool heroMounted,
        out HeroOasisRaidEstimate? estimate)
    {
        estimate = null;
        if (garrison.Count == 0 || heroAttackStrength <= 0 || currentHpPercent is < 1 or > 100
            || garrison.Any(group => group.Count <= 0))
        {
            return false;
        }

        long defenderStrength = 10; // Unoccupied oasis base defence.
        long defenderCount = 0;
        try
        {
            foreach (var group in garrison)
            {
                defenderStrength = checked(defenderStrength + (long)group.Count *
                    (heroMounted ? group.Troop.DefenceAgainstCavalry : group.Troop.DefenceAgainstInfantry));
                defenderCount = checked(defenderCount + group.Count);
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        if (defenderCount == 0)
        {
            return false;
        }

        var totalUnits = defenderCount + 1;
        var exponent = totalUnits <= 1_000
            ? 1.5
            : Math.Clamp(2 * (1.8592 - Math.Pow(totalUnits, 0.015)), 1.2578, 1.5);
        var ratio = (double)defenderStrength / heroAttackStrength;
        var x = Math.Pow(ratio, exponent);
        var attackerLossFraction = x / (1 + x);
        var defenderLossFraction = 1 - attackerLossFraction;
        var predictedHpLoss = (int)Math.Ceiling(attackerLossFraction * 100);
        var predictedHpAfterRaid = Math.Max(0, currentHpPercent - predictedHpLoss);

        long bounty = 0;
        try
        {
            foreach (var group in garrison)
            {
                // Travian rounds losses per unit type, rather than the whole garrison.
                var estimatedKilled = (long)Math.Round(group.Count * defenderLossFraction, MidpointRounding.AwayFromZero);
                bounty = checked(bounty + estimatedKilled * group.Troop.BountyPerResource);
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        if (bounty is <= 0 or > int.MaxValue)
        {
            return false;
        }

        estimate = new HeroOasisRaidEstimate(
            predictedHpAfterRaid,
            predictedHpLoss,
            (int)bounty,
            attackerLossFraction,
            defenderLossFraction);
        return true;
    }
}
