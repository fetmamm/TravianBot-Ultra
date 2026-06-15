using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class ConstructionQueueState
{
    public const string CurrentDeferClassificationVersion = "2";

    public static bool IsActiveQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused;
    }

    public static bool IsQueueOccupancyDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("build queue full", StringComparison.OrdinalIgnoreCase)
                || message.Contains("blocked by queue", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsConstructionInProgressDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("already queued and still in progress", StringComparison.OrdinalIgnoreCase)
                || message.Contains("queued and still in progress", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsQueueOccupancyDeferred(QueueItem item)
    {
        return item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferReason, out var reason)
            && string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonQueueFull, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLegacyQueueOccupancyDeferred(QueueItem item)
    {
        return IsQueueOccupancyDeferred(item)
            && (!item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferClassificationVersion, out var version)
                || !string.Equals(version, CurrentDeferClassificationVersion, StringComparison.Ordinal));
    }

    public static bool ShouldLiveValidateLegacyQueueOccupancy(
        QueueItem item,
        IReadOnlyCollection<QueueItem> confirmedQueueOccupancyBlockers)
    {
        return IsLegacyQueueOccupancyDeferred(item)
            && confirmedQueueOccupancyBlockers.Count == 0;
    }

    public static bool IsConstructionInProgressDeferred(QueueItem item)
    {
        return item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferReason, out var reason)
            && string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonInProgress, StringComparison.OrdinalIgnoreCase);
    }

    public static bool BlocksAdditionalConstruction(QueueItem queueOccupancyDeferredItem)
    {
        return IsQueueOccupancyDeferred(queueOccupancyDeferredItem);
    }

    public static TimeSpan? ResolveQueueFullRetryDelay(VillageStatus status, bool? travianPlusActive)
    {
        var capacity = travianPlusActive == true ? 2 : 1;
        if (status.ActiveBuildCount < capacity)
        {
            return TimeSpan.Zero;
        }

        return status.BuildQueueRemainingSeconds is > 0
            ? TimeSpan.FromSeconds(status.BuildQueueRemainingSeconds.Value)
            : null;
    }

    public static int ResolveDisplayedActiveBuildCount(VillageStatus? status, bool hasQueueFullEvidence)
    {
        if (status is not null && status.ActiveBuildCount > 0)
        {
            return status.ActiveBuildCount;
        }

        return hasQueueFullEvidence ? 1 : 0;
    }

    public static VillageStatus PreserveKnownConstructionState(VillageStatus incoming, VillageStatus existing)
    {
        var hasNoConstructionEvidence = incoming.ActiveBuildCount == 0
            && incoming.BuildQueue.Count == 0
            && (incoming.ActiveConstructions?.Count ?? 0) == 0
            && incoming.BuildQueueRemainingSeconds is null;
        var existingHasConstruction = existing.ActiveBuildCount > 0
            || existing.BuildQueue.Count > 0
            || (existing.ActiveConstructions?.Count ?? 0) > 0
            || existing.BuildQueueRemainingSeconds is > 0;

        if (incoming.ActiveConstructionsFromOverview || !hasNoConstructionEvidence || !existingHasConstruction)
        {
            return incoming;
        }

        return incoming with
        {
            BuildQueue = existing.BuildQueue,
            IsBuildingInProgress = existing.IsBuildingInProgress,
            ActiveBuildCount = existing.ActiveBuildCount,
            BuildQueueRemainingSeconds = existing.BuildQueueRemainingSeconds,
            BuildQueueRemainingText = existing.BuildQueueRemainingText,
            BuildQueueFinish = existing.BuildQueueFinish,
            ActiveConstructions = existing.ActiveConstructions,
            ActiveConstructionsFromOverview = existing.ActiveConstructionsFromOverview,
        };
    }
}
