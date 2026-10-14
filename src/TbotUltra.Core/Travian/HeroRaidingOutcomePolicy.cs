namespace TbotUltra.Core.Travian;

public enum HeroRaidingOutcomeKind
{
    Pending,
    Verified,
    Disable,
}

public sealed record HeroRaidingOutcomeDecision(HeroRaidingOutcomeKind Kind, string Reason);

public sealed record HeroRaidingOutcomeObservation(
    bool OutgoingRaidVerified,
    bool HeroStateKnown,
    bool HeroHomeConfirmed,
    bool HeroDeadConfirmed,
    int? CurrentHpPercent);

/// <summary>
/// Fail-closed decision after one hero oasis raid. The exact outgoing movement must have
/// been confirmed at Rally Point, followed by live home status and HP. The report is not
/// a dependency. One HP point of simulator rounding is tolerated only above the minimum.
/// </summary>
public static class HeroRaidingOutcomePolicy
{
    public static HeroRaidingOutcomeDecision Evaluate(
        HeroRaidingOutcomeObservation observation,
        int predictedHpAfterRaid,
        int minimumHpPercent)
    {
        if (predictedHpAfterRaid is < 1 or > 100 || minimumHpPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(predictedHpAfterRaid));

        if (!observation.OutgoingRaidVerified)
            return new(HeroRaidingOutcomeKind.Disable, "The outgoing hero raid was not verified after final Confirm.");
        if (observation.HeroDeadConfirmed)
            return new(HeroRaidingOutcomeKind.Disable, "The hero died after the oasis raid.");
        if (!observation.HeroStateKnown)
            return new(HeroRaidingOutcomeKind.Disable, "The hero's post-raid state could not be verified.");
        if (!observation.HeroHomeConfirmed)
            return new(HeroRaidingOutcomeKind.Pending, "The hero has not yet returned from the oasis raid.");
        if (observation.CurrentHpPercent is not int hp || hp is < 1 or > 100)
            return new(HeroRaidingOutcomeKind.Disable, "The hero's post-raid HP could not be verified.");
        if (hp < minimumHpPercent)
            return new(HeroRaidingOutcomeKind.Disable,
                $"The hero returned with {hp}% HP, below the configured {minimumHpPercent}% minimum.");
        if (hp < predictedHpAfterRaid - 1)
            return new(HeroRaidingOutcomeKind.Disable,
                $"The hero returned with {hp}% HP, unexpectedly below the predicted {predictedHpAfterRaid}%.");

        return new(HeroRaidingOutcomeKind.Verified, $"Oasis raid verified; hero returned with {hp}% HP.");
    }
}
