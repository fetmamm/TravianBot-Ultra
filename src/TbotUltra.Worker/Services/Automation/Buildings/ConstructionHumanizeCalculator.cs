namespace TbotUltra.Worker.Services;

internal sealed record ConstructionHumanizeDecision(double DelaySeconds, string Reason)
{
    public static ConstructionHumanizeDecision None { get; } = new(0, "no delay");
}

/// <summary>
/// Pure queue-timer decision for construction humanization. Browser/session state stays in
/// <see cref="TravianClient"/>; random selection is injected so production behavior and tests use
/// the same branch logic without coupling this calculator to global RNG state.
/// </summary>
internal static class ConstructionHumanizeCalculator
{
    public static ConstructionHumanizeDecision CalculateAfterFullQueue(
        IReadOnlyList<int> relevantRemainingSeconds,
        int slotFreeWaitSeconds,
        double queuePercentMin,
        double queuePercentMax,
        double maxDelayMinutes,
        double noPlusMinMinutes,
        double noPlusMaxMinutes,
        Func<double, double, double> randomInRange)
    {
        ArgumentNullException.ThrowIfNull(randomInRange);
        if (slotFreeWaitSeconds <= 0)
        {
            return ConstructionHumanizeDecision.None;
        }

        var remainingAfterSlotFrees = relevantRemainingSeconds
            .Select(seconds => seconds - slotFreeWaitSeconds)
            .Where(seconds => seconds > 0)
            .OrderBy(seconds => seconds)
            .ToList();

        if (remainingAfterSlotFrees.Count > 0)
        {
            var referenceSeconds = remainingAfterSlotFrees[0];
            var percent = randomInRange(queuePercentMin, queuePercentMax) / 100.0;
            var delaySeconds = Math.Min(
                referenceSeconds * percent,
                Math.Max(0, maxDelayMinutes) * 60.0);
            return new ConstructionHumanizeDecision(
                delaySeconds,
                $"after slot opens, percent {percent * 100:F0}% of {referenceSeconds}s remaining");
        }

        var minutes = randomInRange(noPlusMinMinutes, noPlusMaxMinutes);
        return new ConstructionHumanizeDecision(
            minutes * 60.0,
            $"after slot opens, no-plus {minutes:F1}m");
    }
}
