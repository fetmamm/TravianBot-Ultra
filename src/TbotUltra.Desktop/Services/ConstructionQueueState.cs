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
    CropShortage,
    Humanize,
    Retry,
}

public sealed record ConstructionQueueSnapshot(
    ConstructionQueueKnowledge Knowledge,
    int ActiveCount,
    int? RemainingSeconds);

public enum ConstructionLoopWaitKind
{
    None,
    DeferredTask,
    QueueFull,
    ActiveQueue,
}

public sealed record ConstructionLoopWait(
    ConstructionLoopWaitKind Kind,
    int? RemainingSeconds,
    QueueItem? DeferredItem = null);

public sealed record ConstructionHumanizeToggleReset(
    Dictionary<string, string> Payload,
    TimeSpan? Delay,
    bool Changed);

public static class ConstructionQueueState
{
    public const string CurrentDeferClassificationVersion = "3";
    private const string PageTimerWaitReason = "page_timer";

    public static bool SupportsIndependentConstructionCategories(VillageStatus? status)
    {
        return string.Equals(status?.Tribe, "Romans", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsResourceConstructionTask(string? taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<Building> MergeObservedBuildingLevels(
        IReadOnlyList<Building> buildings,
        IReadOnlyList<ActiveConstruction> activeConstructions)
    {
        if (buildings.Count == 0 || activeConstructions.Count == 0)
        {
            return buildings;
        }

        var completedLevelsBySlot = activeConstructions
            .Where(item => item.Kind == ConstructionKind.Building
                && item.SlotId is not null
                && item.Level is > 0)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Level).First());
        if (completedLevelsBySlot.Count == 0)
        {
            return buildings;
        }

        var changed = false;
        var merged = buildings
            .Select(building =>
            {
                if (building.SlotId is not int slotId
                    || !completedLevelsBySlot.TryGetValue(slotId, out var active)
                    || !IsSameBuilding(building, active))
                {
                    return building;
                }

                // Travian's overview reports the target level currently under construction. Therefore
                // target - 1 is already complete and safe to retain even if a queued UI projection is cleared.
                var completedLevel = active.Level!.Value - 1;
                if (completedLevel <= (building.Level ?? 0))
                {
                    return building;
                }

                changed = true;
                return building with { Level = completedLevel };
            })
            .ToList();
        return changed ? merged : buildings;
    }

    private static bool IsSameBuilding(Building building, ActiveConstruction active)
    {
        if (building.Gid is int buildingGid && active.Gid is int activeGid)
        {
            return buildingGid == activeGid;
        }

        return string.Equals(building.Name?.Trim(), active.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

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
                || message.Contains("queued upgrade toward", StringComparison.OrdinalIgnoreCase)
                || message.Contains("queued upgrade(s) already reaching target", StringComparison.OrdinalIgnoreCase));
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

    public static bool IsCropShortageDeferMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("wait_reason=crop_shortage", StringComparison.OrdinalIgnoreCase);
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

    public static bool ShouldPrepareConfirmedEmptyQueueHead(QueueItem item, DateTimeOffset now)
    {
        return item.NextAttemptAt > now
            && ResolveDeferReason(item) == ConstructionDeferReason.Resources
            && item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeWaitReason, out var waitReason)
            && string.Equals(waitReason, PageTimerWaitReason, StringComparison.OrdinalIgnoreCase);
    }

    public static QueueItem? SelectFirstUnstartedHead(IEnumerable<QueueItem> items)
    {
        return items.SkipWhile(IsConstructionInProgressDeferred).FirstOrDefault();
    }

    public static IReadOnlyList<QueueItem> SelectResourceDeferredHeadsToRelease(
        IEnumerable<(QueueItem Item, string VillageKey)> items,
        DateTimeOffset now)
    {
        return items
            .GroupBy(entry => entry.VillageKey, StringComparer.OrdinalIgnoreCase)
            .Select(village => SelectFirstUnstartedHead(village.Select(entry => entry.Item)))
            .Where(head => head is not null
                && head.NextAttemptAt > now
                && ResolveDeferReason(head) == ConstructionDeferReason.Resources)
            .Cast<QueueItem>()
            .ToList();
    }

    public static bool HasHeroInventoryIncreased(
        HeroInventoryResources? previous,
        HeroInventoryResources current)
    {
        return previous is not null
            && (current.Wood > previous.Wood
                || current.Clay > previous.Clay
                || current.Iron > previous.Iron
                || current.Crop > previous.Crop);
    }

    public static ConstructionHumanizeToggleReset ResolveHumanizeToggleReset(
        QueueItem item,
        DateTimeOffset now)
    {
        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
        var changed = payload.Remove(BotOptionPayloadKeys.ConstructionLoginFill);
        changed |= payload.Remove(BotOptionPayloadKeys.ConstructionLoginFillExpiresAtUnixSeconds);
        changed |= payload.Remove(BotOptionPayloadKeys.ConstructionPreSleepFill);
        changed |= payload.Remove(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied);

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

        if (string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonCropShortage, StringComparison.OrdinalIgnoreCase))
        {
            return ConstructionDeferReason.CropShortage;
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

    public static ConstructionLoopWait ResolveLoopWait(
        IReadOnlyList<QueueItem> groupItems,
        ConstructionQueueSnapshot snapshot,
        ConstructionQueueAvailability availability,
        DateTimeOffset now)
    {
        var deferred = groupItems
            .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
            .OrderBy(item => item.NextAttemptAt)
            .FirstOrDefault();
        if (deferred is not null)
        {
            return new ConstructionLoopWait(
                ConstructionLoopWaitKind.DeferredTask,
                Math.Max(0, (int)Math.Ceiling((deferred.NextAttemptAt - now).TotalSeconds)),
                deferred);
        }

        if (availability == ConstructionQueueAvailability.Full)
        {
            return new ConstructionLoopWait(ConstructionLoopWaitKind.QueueFull, snapshot.RemainingSeconds);
        }

        return snapshot.Knowledge == ConstructionQueueKnowledge.Active
            ? new ConstructionLoopWait(ConstructionLoopWaitKind.ActiveQueue, snapshot.RemainingSeconds)
            : new ConstructionLoopWait(ConstructionLoopWaitKind.None, null);
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

        var isResourceTask = IsResourceConstructionTask(item.TaskName);
        var active = ResolveCurrentActiveConstructions(status, now);
        var relevantCount = isResourceTask
            ? active.Count(construction => construction.Kind == ConstructionKind.Resource)
            : active.Count(construction => construction.Kind != ConstructionKind.Resource);
        var categoryCapacity = travianPlusActive == true ? 2 : 1;
        var totalCapacity = travianPlusActive == true ? 3 : 2;
        return relevantCount < categoryCapacity && active.Count < totalCapacity
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
            var isResourceTask = IsResourceConstructionTask(item.TaskName);
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
