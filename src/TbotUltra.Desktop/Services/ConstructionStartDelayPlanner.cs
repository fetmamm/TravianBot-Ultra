using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed record ConstructionStartDelayDecision(
    int DelaySeconds,
    DateTimeOffset ReferenceFinishUtc,
    DateTimeOffset ReadyAtUtc,
    string Reason);

public sealed record ConstructionFullQueueDelayDecision(
    int QueueRetrySeconds,
    int HumanizeExtraSeconds,
    DateTimeOffset ReferenceFinishUtc,
    DateTimeOffset ReadyAtUtc,
    string Reason);

/// <summary>
/// Plans the normal construction start pause while the bot is still outside the target village.
/// The caller persists the result; previews deliberately do not invoke this planner.
/// </summary>
public static class ConstructionStartDelayPlanner
{
    public static ConstructionFullQueueDelayDecision? ResolveAfterFullQueue(
        QueueItem item,
        VillageStatus? status,
        bool? travianPlusActive,
        BotOptions options,
        DateTimeOffset now,
        Func<double, double, double> randomInRange)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(randomInRange);

        if (!options.ConstructionHumanizeDelayEnabled
            || status?.ActiveConstructionsFromOverview != true
            || item.Status != QueueStatus.Pending
            || item.NextAttemptAt > now
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionLoginFill)
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill)
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied)
            || ConstructionQueueState.ResolveQueueHumanizeExtraSeconds(item) > 0
            || ConstructionQueueState.ResolveAvailabilityForItem(status, travianPlusActive, item, now)
                != ConstructionQueueAvailability.Full)
        {
            return null;
        }

        var active = ConstructionQueueState.ResolveCurrentActiveConstructions(status, now);
        if (string.Equals(status?.Tribe, "Romans", StringComparison.OrdinalIgnoreCase))
        {
            var resourceTask = ConstructionQueueState.IsResourceConstructionTask(item.TaskName);
            active = active.Where(construction => resourceTask
                ? construction.Kind == ConstructionKind.Resource
                : construction.Kind != ConstructionKind.Resource).ToList();
            // A Roman lane can also be blocked by the village-wide cap. In that case the
            // first freed slot may be in the other lane, so leave timing to live validation.
            if (active.Count < (travianPlusActive == true ? 2 : 1))
            {
                return null;
            }
        }

        var remaining = active.Select(construction => construction.Finish?.RemainingSecondsAt(now)
                ?? construction.TimeLeftSeconds ?? 0)
            .ToList();
        // Do not infer an exact wake-up from an unknown or incomplete timer snapshot.
        if (remaining.Count == 0 || remaining.Any(seconds => seconds <= 0))
        {
            return null;
        }

        var slotFreeSeconds = remaining.Min();
        var decision = ConstructionHumanizeCalculator.CalculateAfterFullQueue(
            remaining,
            slotFreeSeconds,
            options.ConstructionHumanizeQueuePercentMin,
            options.ConstructionHumanizeQueuePercentMax,
            options.ConstructionHumanizeMaxDelayMinutes,
            options.ConstructionHumanizeNoPlusMinMinutes,
            options.ConstructionHumanizeNoPlusMaxMinutes,
            randomInRange);
        var extraSeconds = Math.Max(0, decision.QueueRetrySeconds - slotFreeSeconds);
        if (extraSeconds < 1)
        {
            return null;
        }

        var retrySeconds = decision.QueueRetrySeconds + 1; // Same timer-race buffer as the live worker.
        return new ConstructionFullQueueDelayDecision(
            retrySeconds,
            extraSeconds + 1,
            now.AddSeconds(slotFreeSeconds),
            now.AddSeconds(retrySeconds),
            decision.Reason);
    }

    public static ConstructionStartDelayDecision? Resolve(
        QueueItem item,
        VillageStatus? status,
        bool? travianPlusActive,
        BotOptions options,
        DateTimeOffset now,
        Func<double, double, double> randomInRange)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(randomInRange);

        if (!options.ConstructionHumanizeDelayEnabled
            || item.Status != QueueStatus.Pending
            || item.NextAttemptAt > now
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionLoginFill)
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill)
            || item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied)
            || ConstructionQueueState.IsConstructionHumanizeDeferred(item)
            || ConstructionQueueState.ResolveQueueHumanizeExtraSeconds(item) > 0
            || ConstructionQueueState.ResolveAvailabilityForItem(status, travianPlusActive, item, now)
                != ConstructionQueueAvailability.Available)
        {
            return null;
        }

        var active = ConstructionQueueState.ResolveCurrentActiveConstructions(status, now);
        if (string.Equals(status?.Tribe, "Romans", StringComparison.OrdinalIgnoreCase))
        {
            var resourceTask = string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);
            active = active
                .Where(construction => resourceTask
                    ? construction.Kind == ConstructionKind.Resource
                    : construction.Kind != ConstructionKind.Resource)
                .ToList();
        }

        var referenceSeconds = active
            .Select(construction => construction.Finish?.RemainingSecondsAt(now)
                ?? construction.TimeLeftSeconds
                ?? 0)
            .Where(seconds => seconds > 1)
            .DefaultIfEmpty(0)
            .Min();
        if (referenceSeconds <= 1)
        {
            return null;
        }

        var delay = ConstructionHumanizeCalculator.CalculateBoundedQueueDelaySeconds(
            referenceSeconds,
            options.ConstructionHumanizeQueuePercentMin,
            options.ConstructionHumanizeQueuePercentMax,
            options.ConstructionHumanizeMaxDelayMinutes,
            randomInRange);
        if (delay < 1)
        {
            return null;
        }

        var delaySeconds = (int)Math.Ceiling(delay);
        var selectedPercent = delay / referenceSeconds * 100;
        return new ConstructionStartDelayDecision(
            delaySeconds,
            now.AddSeconds(referenceSeconds),
            now.AddSeconds(delaySeconds),
            $"percent {selectedPercent:F0}% of {referenceSeconds}s remaining");
    }
}
