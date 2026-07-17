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
    StorageCapacity,
    Humanize,
    Retry,
}

public sealed record ConstructionQueueSnapshot(
    ConstructionQueueKnowledge Knowledge,
    int ActiveCount,
    int? RemainingSeconds);

public sealed record ConstructionHumanizeToggleReset(
    Dictionary<string, string> Payload,
    TimeSpan? Delay,
    bool Changed);

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

    // The worker's humanize gate deferred this start (slot free, only waiting out the human pause).
    // These are the items the pre-sleep fill sweep may pull forward so the slot is used before sleep.
    public static bool IsConstructionHumanizeDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("humanized construction start delay", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesConstructionHumanizeStartGate(string? taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsConstructionStorageCapacityDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains($"wait_reason={BotOptionPayloadKeys.UpgradeDeferReasonStorageCapacity}", StringComparison.OrdinalIgnoreCase)
                || message.Contains(BotOptionPayloadKeys.UpgradeStorageCapacityKind, StringComparison.OrdinalIgnoreCase)
                || message.Contains("Extend warehouse", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Extend granary", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Extend silo", StringComparison.OrdinalIgnoreCase)
                || message.Contains("warehouse first", StringComparison.OrdinalIgnoreCase)
                || message.Contains("granary first", StringComparison.OrdinalIgnoreCase)
                || message.Contains("silo first", StringComparison.OrdinalIgnoreCase));
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

    public static bool IsStorageCapacityDeferred(QueueItem item)
    {
        return ResolveDeferReason(item) == ConstructionDeferReason.StorageCapacity;
    }

    public static bool IsConstructionRequirementDeferred(QueueItem item)
    {
        return ResolveDeferReason(item) == ConstructionDeferReason.Requirements;
    }

    public static bool IsConstructionHumanizeDeferred(QueueItem item)
    {
        return ResolveDeferReason(item) == ConstructionDeferReason.Humanize;
    }

    public static bool ShouldPrepareLoginFill(QueueItem item, DateTimeOffset now)
    {
        if (item.NextAttemptAt <= now || IsConstructionHumanizeDeferred(item))
        {
            return true;
        }

        return IsQueueOccupancyDeferred(item);
    }

    public static ConstructionHumanizeToggleReset ResolveHumanizeToggleReset(
        QueueItem item,
        DateTimeOffset now)
    {
        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
        var changed = payload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
        changed |= payload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);
        changed |= payload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);

        var extraSeconds = ResolveQueueHumanizeExtraSeconds(item);
        changed |= payload.Remove(BotOptionPayloadKeys.QueueHumanizeExtraSeconds);

        TimeSpan? delay = null;
        if (IsConstructionHumanizeDeferred(item))
        {
            changed |= payload.Remove(BotOptionPayloadKeys.UpgradeDeferReason);
            changed |= payload.Remove(BotOptionPayloadKeys.UpgradeDeferClassificationVersion);
            delay = TimeSpan.Zero;
        }
        else if (extraSeconds > 0)
        {
            var remaining = Math.Max(0, (item.NextAttemptAt - now).TotalSeconds);
            delay = TimeSpan.FromSeconds(Math.Max(0, remaining - extraSeconds));
        }

        return new ConstructionHumanizeToggleReset(payload, delay, changed);
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

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonStorageCapacity, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.StorageCapacity;
        }

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonHumanize, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.Humanize;
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

        var allConstructions = status.ActiveConstructions ?? [];
        var activeConstructions = ResolveCurrentActiveConstructions(status, now);
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

        if (allConstructions.Count > 0)
        {
            return new ConstructionQueueSnapshot(ConstructionQueueKnowledge.Unknown, 0, null);
        }

        return status.ActiveConstructionsFromOverview
            ? new ConstructionQueueSnapshot(ConstructionQueueKnowledge.ConfirmedEmpty, 0, null)
            : new ConstructionQueueSnapshot(ConstructionQueueKnowledge.Unknown, 0, null);
    }

    public static IReadOnlyList<ActiveConstruction> ResolveCurrentActiveConstructions(
        VillageStatus? status,
        DateTimeOffset? now = null)
    {
        if (status?.ActiveConstructions is not { Count: > 0 } activeConstructions)
        {
            return [];
        }

        var capturedAt = now ?? DateTimeOffset.UtcNow;
        return activeConstructions
            .Where(item =>
            {
                if (item.Finish is not null)
                {
                    return !item.Finish.IsFinishedAt(capturedAt);
                }

                return item.TimeLeftSeconds is not int seconds || seconds > 0;
            })
            .ToList();
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

    public static ConstructionQueueAvailability ResolveAvailabilityForItem(
        VillageStatus? status,
        bool? travianPlusActive,
        QueueItem item,
        DateTimeOffset? now = null)
    {
        if (status is null
            || !string.Equals(status.Tribe, "Romans", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveAvailability(status, travianPlusActive, now);
        }

        var snapshot = ResolveSnapshot(status, now);
        if (snapshot.Knowledge == ConstructionQueueKnowledge.Unknown)
        {
            return ConstructionQueueAvailability.Unknown;
        }

        var isResourceTask = string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);
        var active = ResolveCurrentActiveConstructions(status, now);
        var relevantCount = isResourceTask
            ? active.Count(construction => construction.Kind == ConstructionKind.Resource)
            : active.Count(construction => construction.Kind != ConstructionKind.Resource);
        var capacity = isResourceTask ? 1 : travianPlusActive == true ? 2 : 1;
        return relevantCount < capacity
            ? ConstructionQueueAvailability.Available
            : ConstructionQueueAvailability.Full;
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

    public static TimeSpan? ResolveQueueFullRetryDelay(
        VillageStatus status,
        bool? travianPlusActive,
        QueueItem item,
        DateTimeOffset? now = null)
    {
        var capturedAt = now ?? DateTimeOffset.UtcNow;
        var availability = ResolveAvailabilityForItem(status, travianPlusActive, item, capturedAt);
        if (availability == ConstructionQueueAvailability.Unknown)
        {
            return null;
        }

        var extraSeconds = ResolveQueueHumanizeExtraSeconds(item);
        if (availability == ConstructionQueueAvailability.Available)
        {
            if (extraSeconds <= 0)
            {
                return TimeSpan.Zero;
            }

            var storedRemaining = item.NextAttemptAt - capturedAt;
            return storedRemaining > TimeSpan.Zero ? storedRemaining : TimeSpan.Zero;
        }

        var active = ResolveCurrentActiveConstructions(status, capturedAt);
        if (string.Equals(status.Tribe, "Romans", StringComparison.OrdinalIgnoreCase))
        {
            var isResourceTask = string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);
            active = active
                .Where(construction => isResourceTask
                    ? construction.Kind == ConstructionKind.Resource
                    : construction.Kind != ConstructionKind.Resource)
                .ToList();
        }

        var liveSeconds = active
            .Select(construction => construction.Finish?.RemainingSecondsAt(capturedAt)
                ?? construction.TimeLeftSeconds
                ?? 0)
            .Where(seconds => seconds > 0)
            .DefaultIfEmpty(0)
            .Min();
        return liveSeconds > 0
            ? TimeSpan.FromSeconds(liveSeconds + extraSeconds)
            : null;
    }

    public static int ResolveQueueHumanizeExtraSeconds(QueueItem item)
    {
        return item.Payload.TryGetValue(BotOptionPayloadKeys.QueueHumanizeExtraSeconds, out var raw)
            && int.TryParse(raw, out var seconds)
            ? Math.Max(0, seconds)
            : 0;
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
