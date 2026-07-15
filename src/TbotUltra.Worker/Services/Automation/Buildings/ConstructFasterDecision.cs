using TbotUltra.Core.Configuration;

namespace TbotUltra.Worker.Services;

internal sealed record ConstructFasterDecisionResult(bool UseVideo, string Reason);

internal sealed record ConstructFasterVerificationDecisionResult(
    bool ActionRegistered,
    bool BonusConfirmed);

internal static class ConstructFasterDecision
{
    internal static ConstructFasterVerificationDecisionResult ResolveVerifiedOutcome(
        bool videoCompleted,
        bool targetConstructionVerified)
        => new(
            ActionRegistered: targetConstructionVerified,
            BonusConfirmed: videoCompleted && targetConstructionVerified);

    public static ConstructFasterDecisionResult Evaluate(
        BotOptions options,
        int durationSeconds,
        bool buttonPresent,
        bool buttonDisabled,
        int? randomRoll = null)
    {
        if (!options.ConstructFasterEnabled)
        {
            return new ConstructFasterDecisionResult(false, "feature disabled");
        }

        var minSeconds = options.ConstructFasterMinBuildTimeEnabled
            ? Math.Max(0, options.ConstructFasterMinBuildMinutes) * 60
            : -1;
        if (minSeconds >= 0 && durationSeconds <= minSeconds)
        {
            return new ConstructFasterDecisionResult(false, $"duration {durationSeconds}s <= minimum {minSeconds}s");
        }

        if (!buttonPresent)
        {
            return new ConstructFasterDecisionResult(false, "video button missing");
        }

        if (buttonDisabled)
        {
            return new ConstructFasterDecisionResult(false, "video button disabled");
        }

        if (!options.ConstructFasterRandomEnabled)
        {
            return new ConstructFasterDecisionResult(true, "enabled");
        }

        var chance = Math.Clamp(options.ConstructFasterRandomChancePercent, 0, 100);
        if (chance <= 0)
        {
            return new ConstructFasterDecisionResult(false, "random chance 0%");
        }

        if (chance >= 100)
        {
            return new ConstructFasterDecisionResult(true, "random chance 100%");
        }

        var roll = Math.Clamp(randomRoll ?? Random.Shared.Next(0, 100), 0, 99);
        return roll < chance
            ? new ConstructFasterDecisionResult(true, $"random roll {roll} < chance {chance}")
            : new ConstructFasterDecisionResult(false, $"random roll {roll} >= chance {chance}");
    }
}
