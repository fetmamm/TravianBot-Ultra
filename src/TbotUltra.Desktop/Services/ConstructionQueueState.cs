using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class ConstructionQueueState
{
    public static bool IsActiveQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused;
    }

    public static bool IsQueueOccupancyDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("build queue full", StringComparison.OrdinalIgnoreCase)
                || message.Contains("blocked by queue", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already queued", StringComparison.OrdinalIgnoreCase)
                || message.Contains("queued and still in progress", StringComparison.OrdinalIgnoreCase)
                || message.Contains("upgrade toward max already queued", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsQueueOccupancyDeferred(QueueItem item)
    {
        return item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferReason, out var reason)
            && string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonQueueFull, StringComparison.OrdinalIgnoreCase);
    }

    public static VillageStatus PreserveKnownConstructionState(VillageStatus incoming, VillageStatus existing)
    {
        var isPartialRead = incoming.Buildings.Count == 0;
        var hasNoConstructionEvidence = incoming.ActiveBuildCount == 0
            && incoming.BuildQueue.Count == 0
            && (incoming.ActiveConstructions?.Count ?? 0) == 0
            && incoming.BuildQueueRemainingSeconds is null;
        var existingHasConstruction = existing.ActiveBuildCount > 0
            || existing.BuildQueue.Count > 0
            || (existing.ActiveConstructions?.Count ?? 0) > 0
            || existing.BuildQueueRemainingSeconds is > 0;

        if (!isPartialRead || !hasNoConstructionEvidence || !existingHasConstruction)
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
            ActiveConstructions = existing.ActiveConstructions,
        };
    }
}
