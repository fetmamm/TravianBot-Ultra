namespace TbotUltra.Worker.Services;

internal sealed record AdventureVideoChanceDecisionResult(
    bool RunVideo,
    int ChancePercent,
    int Roll,
    string Reason);

internal static class AdventureVideoChanceDecision
{
    internal static AdventureVideoChanceDecisionResult Evaluate(int chancePercent, int roll)
    {
        var chance = Math.Clamp(chancePercent, 0, 100);
        var normalizedRoll = Math.Clamp(roll, 0, 99);
        var run = normalizedRoll < chance;
        return new AdventureVideoChanceDecisionResult(
            run,
            chance,
            normalizedRoll,
            run
                ? $"random roll {normalizedRoll} < chance {chance}"
                : $"random roll {normalizedRoll} >= chance {chance}");
    }
}
