using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public enum ConstructionQueueKnowledge
{
    Unknown,
    ConfirmedEmpty,
    Active,
}

public enum ConstructionQueueAvailability
{
    Unknown,
    Available,
    Full,
}

public enum ConstructionDeferReason
{
    None,
    QueueFull,
    InProgress,
    Resources,
    Requirements,
    Retry,
}

public sealed record ConstructionQueueSnapshot(
    ConstructionQueueKnowledge Knowledge,
    int ActiveCount,
    int? RemainingSeconds);

public static class ConstructionQueueState
{
    public const string CurrentDeferClassificationVersion = "3";

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
                || message.Contains("queued and still in progress", StringComparison.OrdinalIgnoreCase)
                || message.Contains("queued upgrade toward", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsConstructionRequirementDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("missing requirements", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cannot be built yet", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cannot be upgraded yet", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsConstructionResourceDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("needs resources", StringComparison.OrdinalIgnoreCase)
                || message.Contains("blocked by resources", StringComparison.OrdinalIgnoreCase)
                || message.Contains("resource wait", StringComparison.OrdinalIgnoreCase)
                || message.Contains("wait_reason=", StringComparison.OrdinalIgnoreCase)
                || message.Contains("upgrade_required_", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsQueueOccupancyDeferred(QueueItem item)
    {
        return ResolveDeferReason(item) == ConstructionDeferReason.QueueFull;
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
        return ResolveDeferReason(item) == ConstructionDeferReason.InProgress;
    }

    public static bool BlocksAdditionalConstruction(QueueItem queueOccupancyDeferredItem)
    {
        return IsQueueOccupancyDeferred(queueOccupancyDeferredItem);
    }

    public static ConstructionDeferReason ResolveDeferReason(QueueItem item)
    {
        if (!item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferReason, out var reason))
        {
            return ConstructionDeferReason.None;
        }

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonQueueFull, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.QueueFull;
        }

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonInProgress, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.InProgress;
        }

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonResources, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.Resources;
        }

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonRequirements, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.Requirements;
        }

        return ConstructionDeferReason.Retry;
    }

    public static ConstructionQueueSnapshot ResolveSnapshot(
        VillageStatus? status,
        DateTimeOffset? now = null)
    {
        if (status is null)
        {
            return new ConstructionQueueSnapshot(ConstructionQueueKnowledge.Unknown, 0, null);
        }

        var activeConstructions = status.ActiveConstructions ?? [];
        if (activeConstructions.Count > 0)
        {
            var capturedAt = now ?? DateTimeOffset.UtcNow;
            var remainingSeconds = activeConstructions
                .Select(item => item.Finish?.RemainingSecondsAt(capturedAt) ?? item.TimeLeftSeconds ?? 0)
                .Where(seconds => seconds > 0)
                .DefaultIfEmpty(0)
                .Min();
            return new ConstructionQueueSnapshot(
                ConstructionQueueKnowledge.Active,
                activeConstructions.Count,
                remainingSeconds > 0 ? remainingSeconds : null);
        }

        return status.ActiveConstructionsFromOverview
            ? new ConstructionQueueSnapshot(ConstructionQueueKnowledge.ConfirmedEmpty, 0, null)
            : new ConstructionQueueSnapshot(ConstructionQueueKnowledge.Unknown, 0, null);
    }

    public static ConstructionQueueAvailability ResolveAvailability(
        VillageStatus? status,
        bool? travianPlusActive,
        DateTimeOffset? now = null)
    {
        var snapshot = ResolveSnapshot(status, now);
        if (snapshot.Knowledge == ConstructionQueueKnowledge.ConfirmedEmpty)
        {
            return ConstructionQueueAvailability.Available;
        }

        if (snapshot.Knowledge == ConstructionQueueKnowledge.Unknown)
        {
            return ConstructionQueueAvailability.Unknown;
        }

        if (snapshot.ActiveCount >= 2)
        {
            return ConstructionQueueAvailability.Full;
        }

        return travianPlusActive switch
        {
            true => ConstructionQueueAvailability.Available,
            false => ConstructionQueueAvailability.Full,
            _ => ConstructionQueueAvailability.Unknown,
        };
    }

    public static TimeSpan? ResolveQueueFullRetryDelay(VillageStatus status, bool? travianPlusActive)
    {
        var availability = ResolveAvailability(status, travianPlusActive);
        if (availability == ConstructionQueueAvailability.Available)
        {
            return TimeSpan.Zero;
        }

        if (availability == ConstructionQueueAvailability.Unknown)
        {
            return null;
        }

        var snapshot = ResolveSnapshot(status);
        return snapshot.RemainingSeconds is > 0
            ? TimeSpan.FromSeconds(snapshot.RemainingSeconds.Value)
            : null;
    }

    public static int ResolveDisplayedActiveBuildCount(VillageStatus? status)
    {
        return ResolveSnapshot(status).ActiveCount;
    }

    public static (int ActiveCount, int? RemainingSeconds) ResolveLiveConstructionTimer(VillageStatus? status)
    {
        var snapshot = ResolveSnapshot(status);
        return (snapshot.ActiveCount, snapshot.RemainingSeconds);
    }

    public static VillageStatus PreserveKnownConstructionState(VillageStatus incoming, VillageStatus existing)
    {
        var incomingSnapshot = ResolveSnapshot(incoming);
        var existingSnapshot = ResolveSnapshot(existing);
        if (incomingSnapshot.Knowledge != ConstructionQueueKnowledge.Unknown
            || existingSnapshot.Knowledge != ConstructionQueueKnowledge.Active)
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
