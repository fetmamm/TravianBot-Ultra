using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionQueueStateTests
{
    [Theory]
    [InlineData("Slot 20: build queue full. queue_wait_seconds=3600")]
    [InlineData("Slot 20 blocked by queue. queue_wait_seconds=3600")]
    public void IsQueueOccupancyDeferMessage_RecognizesQueueWaits(string message)
    {
        Assert.True(ConstructionQueueState.IsQueueOccupancyDeferMessage(message));
    }

    [Theory]
    [InlineData("Slot 20: upgrade to level 5 already queued and still in progress. queue_wait_seconds=3600")]
    [InlineData("Slot 20: upgrade toward max already queued and still in progress. queue_wait_seconds=3600")]
    [InlineData("Slot 20: upgrade to level 5 queued and still in progress. queue_wait_seconds=3600")]
    public void IsConstructionInProgressDeferMessage_RecognizesItemSpecificWaits(string message)
    {
        Assert.True(ConstructionQueueState.IsConstructionInProgressDeferMessage(message));
        Assert.False(ConstructionQueueState.IsQueueOccupancyDeferMessage(message));
    }

    [Fact]
    public void IsConstructionInProgressDeferMessage_RecognizesResourceUpgradeStarted()
    {
        Assert.True(ConstructionQueueState.IsConstructionInProgressDeferMessage(
            "Resource slot 4: queued upgrade toward level 10. queue_wait_seconds=900"));
    }

    [Fact]
    public void IsQueueOccupancyDeferMessage_DoesNotClassifyResourceWait()
    {
        Assert.False(ConstructionQueueState.IsQueueOccupancyDeferMessage(
            "Building slot 20 needs resources. queue_wait_seconds=3600"));
    }

    [Theory]
    [InlineData("Building slot 25 (Residence) upgrade to level 12: blocked by resources. queue_wait_seconds=60 upgrade_wait_reason=storage_capacity upgrade_storage_capacity_kind=warehouse")]
    [InlineData("Resource slot 5 (Clay Pit) upgrade to level 15: Extend warehouse and granary first. queue_wait_seconds=60")]
    public void IsConstructionStorageCapacityDeferMessage_RecognizesStorageBlocks(string message)
    {
        Assert.True(ConstructionQueueState.IsConstructionStorageCapacityDeferMessage(message));
    }

    [Theory]
    [InlineData(QueueStatus.Pending, true)]
    [InlineData(QueueStatus.Running, true)]
    [InlineData(QueueStatus.Paused, true)]
    [InlineData(QueueStatus.Succeeded, false)]
    [InlineData(QueueStatus.Canceled, false)]
    public void IsActiveQueueStatus_MatchesVisibleQueueState(QueueStatus status, bool expected)
    {
        Assert.Equal(expected, ConstructionQueueState.IsActiveQueueStatus(status));
    }

    [Fact]
    public void BlocksAdditionalConstruction_QueueFullBlocksConstruction()
    {
        var deferredBuilding = CreateDeferredQueueFullItem("upgrade_building_to_level");

        Assert.True(ConstructionQueueState.BlocksAdditionalConstruction(deferredBuilding));
    }

    [Fact]
    public void IsLegacyQueueOccupancyDeferred_RequiresCurrentClassificationVersion()
    {
        var legacy = CreateDeferredQueueFullItem("upgrade_building_to_level");
        var current = CreateDeferredQueueFullItem("upgrade_building_to_level");
        current.Payload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
            ConstructionQueueState.CurrentDeferClassificationVersion;

        Assert.True(ConstructionQueueState.IsLegacyQueueOccupancyDeferred(legacy));
        Assert.False(ConstructionQueueState.IsLegacyQueueOccupancyDeferred(current));
    }

    [Fact]
    public void ShouldLiveValidateLegacyQueueOccupancy_StopsAfterConfirmedVillageBlocker()
    {
        var legacy = CreateDeferredQueueFullItem("upgrade_building_to_level");
        var confirmed = CreateDeferredQueueFullItem("upgrade_building_to_level");
        confirmed.Payload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
            ConstructionQueueState.CurrentDeferClassificationVersion;

        Assert.True(ConstructionQueueState.ShouldLiveValidateLegacyQueueOccupancy(legacy, []));
        Assert.False(ConstructionQueueState.ShouldLiveValidateLegacyQueueOccupancy(legacy, [confirmed]));
    }

    [Fact]
    public void BlocksAdditionalConstruction_ResourceWaitDoesNotBlockConstruction()
    {
        var resourceWait = new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonResources,
            },
        };

        Assert.False(ConstructionQueueState.BlocksAdditionalConstruction(resourceWait));
    }

    [Fact]
    public void IsConstructionInProgressDeferred_DoesNotBlockAdditionalConstruction()
    {
        var item = new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonInProgress,
            },
        };

        Assert.True(ConstructionQueueState.IsConstructionInProgressDeferred(item));
        Assert.False(ConstructionQueueState.BlocksAdditionalConstruction(item));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ResolveQueueFullRetryDelay_FreeSlotRetriesImmediately(int activeBuildCount, bool plusActive)
    {
        IReadOnlyList<ActiveConstruction> activeConstructions = activeBuildCount == 0
            ? []
            :
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 4, 900, "00:15:00"),
            ];
        var status = CreateStatus([], [], activeBuildCount, remainingSeconds: 900) with
        {
            ActiveConstructions = activeConstructions,
            ActiveConstructionsFromOverview = true,
        };

        Assert.Equal(TimeSpan.Zero, ConstructionQueueState.ResolveQueueFullRetryDelay(status, plusActive));
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_FullQueueUsesLiveShortestTimer()
    {
        var status = CreateStatus([], [], activeBuildCount: 2, remainingSeconds: 900) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 4, 900, "00:15:00"),
                new ActiveConstruction(ConstructionKind.Building, "Granary", 3, 1200, "00:20:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };

        Assert.Equal(TimeSpan.FromSeconds(900), ConstructionQueueState.ResolveQueueFullRetryDelay(status, travianPlusActive: true));
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_FullQueueWithoutTimerKeepsExistingRetry()
    {
        var status = CreateStatus([], [], activeBuildCount: 1, remainingSeconds: null) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 4, null, null),
            ],
            ActiveConstructionsFromOverview = true,
        };

        Assert.Null(ConstructionQueueState.ResolveQueueFullRetryDelay(status, travianPlusActive: false));
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_ConfirmedEmptyIgnoresStaleLegacyCount()
    {
        var status = CreateStatus([], [], activeBuildCount: 2, remainingSeconds: 900) with
        {
            ActiveConstructions = [],
            ActiveConstructionsFromOverview = true,
        };

        Assert.Equal(TimeSpan.Zero, ConstructionQueueState.ResolveQueueFullRetryDelay(status, travianPlusActive: true));
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_UnknownStatusKeepsExistingRetry()
    {
        var status = CreateStatus([], [], activeBuildCount: 2, remainingSeconds: 900);

        Assert.Null(ConstructionQueueState.ResolveQueueFullRetryDelay(status, travianPlusActive: true));
    }

    [Fact]
    public void ResolveDisplayedActiveBuildCount_IgnoresLegacyConstructionFields()
    {
        var status = CreateStatus(
            buildings: [],
            buildQueue: [new BuildQueueItem("Warehouse level 19", "00:42:41")],
            activeBuildCount: 1,
            remainingSeconds: 2561);

        Assert.Equal(0, ConstructionQueueState.ResolveDisplayedActiveBuildCount(status));
    }

    [Fact]
    public void ResolveDisplayedActiveBuildCount_UnknownStatusIsZero()
    {
        Assert.Equal(0, ConstructionQueueState.ResolveDisplayedActiveBuildCount(null));
    }

    [Fact]
    public void ResolveDisplayedActiveBuildCount_DoesNotKeepExpiredCachedConstructionActive()
    {
        var now = new DateTimeOffset(2026, 6, 16, 20, 0, 0, TimeSpan.Zero);
        var status = CreateStatus([], [], activeBuildCount: 1, remainingSeconds: 0) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Warehouse",
                    19,
                    null,
                    null,
                    TimerSnapshot.FromRemaining(60, now.AddMinutes(-5))),
            ],
            ActiveConstructionsFromOverview = true,
        };

        var snapshot = ConstructionQueueState.ResolveSnapshot(status, now);

        Assert.Equal(ConstructionQueueKnowledge.Unknown, snapshot.Knowledge);
        Assert.Equal(0, snapshot.ActiveCount);
        Assert.Empty(ConstructionQueueState.ResolveCurrentActiveConstructions(status, now));
    }

    [Fact]
    public void ResolveLiveConstructionTimer_IgnoresStaleLegacyTimerWhenActiveQueueIsEmpty()
    {
        var status = CreateStatus([], [], activeBuildCount: 1, remainingSeconds: 3600) with
        {
            ActiveConstructions = [],
            ActiveConstructionsFromOverview = true,
        };

        var result = ConstructionQueueState.ResolveLiveConstructionTimer(status);

        Assert.Equal(0, result.ActiveCount);
        Assert.Null(result.RemainingSeconds);
    }

    [Fact]
    public void ResolveLiveConstructionTimer_UsesActiveConstructionsAsSourceOfTruth()
    {
        var status = CreateStatus([], [], activeBuildCount: 2, remainingSeconds: 3600) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 4, 900, "00:15:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };

        var result = ConstructionQueueState.ResolveLiveConstructionTimer(status);

        Assert.Equal(1, result.ActiveCount);
        Assert.Equal(900, result.RemainingSeconds);
    }

    [Fact]
    public void PreserveKnownConstructionState_KeepsActiveQueueForPartialRead()
    {
        var existing = CreateStatus(
            buildings: [new Building(19, "Main Building", 5, "g15", 15)],
            buildQueue: [new BuildQueueItem("Main Building level 6", "02:00:00")],
            activeBuildCount: 1,
            remainingSeconds: 7200) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Main Building", 6, 7200, "02:00:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var partial = CreateStatus(
            buildings: [],
            buildQueue: [],
            activeBuildCount: 0,
            remainingSeconds: null);

        var result = ConstructionQueueState.PreserveKnownConstructionState(partial, existing);

        Assert.Equal(1, result.ActiveBuildCount);
        Assert.Equal(7200, result.BuildQueueRemainingSeconds);
        Assert.Single(result.BuildQueue);
    }

    [Fact]
    public void PreserveKnownConstructionState_AllowsFullReadToClearCompletedQueue()
    {
        var existing = CreateStatus(
            buildings: [new Building(19, "Main Building", 5, "g15", 15)],
            buildQueue: [new BuildQueueItem("Main Building level 6", "02:00:00")],
            activeBuildCount: 1,
            remainingSeconds: 7200) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Main Building", 6, 7200, "02:00:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var full = CreateStatus(
            buildings: [new Building(19, "Main Building", 6, "g15", 15)],
            buildQueue: [],
            activeBuildCount: 0,
            remainingSeconds: null) with
        {
            ActiveConstructionsFromOverview = true,
        };

        var result = ConstructionQueueState.PreserveKnownConstructionState(full, existing);

        Assert.Equal(0, result.ActiveBuildCount);
        Assert.Null(result.BuildQueueRemainingSeconds);
        Assert.Empty(result.BuildQueue);
    }

    [Fact]
    public void PreserveKnownConstructionState_FullNonOverviewReadCannotClearBrowserQueue()
    {
        var finish = TimerSnapshot.FromRemaining(600);
        var existing = CreateStatus(
            buildings: [new Building(19, "Main Building", 5, "g15", 15)],
            buildQueue: [new BuildQueueItem("Main Building level 6", "00:10:00")],
            activeBuildCount: 1,
            remainingSeconds: 600) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Main Building",
                    6,
                    600,
                    "00:10:00",
                    finish),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var nonOverview = CreateStatus(
            buildings: [new Building(19, "Main Building", 5, "g15", 15)],
            buildQueue: [],
            activeBuildCount: 0,
            remainingSeconds: null);

        var result = ConstructionQueueState.PreserveKnownConstructionState(nonOverview, existing);

        Assert.Single(result.ActiveConstructions!);
        Assert.Equal(1, result.ActiveBuildCount);
        Assert.True(result.ActiveConstructionsFromOverview);
    }

    [Fact]
    public void ResolveAvailabilityForItem_RomansKeepsFullResourceQueueBlockedWhenBuildingSlotIsFree()
    {
        var status = CreateStatus([], [], 1, 600) with
        {
            Tribe = "Romans",
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Resource, "Cropland", 5, 600, "00:10:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var resourceItem = new QueueItem { TaskName = "upgrade_all_resources_to_level" };

        var result = ConstructionQueueState.ResolveAvailabilityForItem(status, true, resourceItem);

        Assert.Equal(ConstructionQueueAvailability.Full, result);
    }

    [Fact]
    public void ResolveAvailabilityForItem_RomansAllowsBuildingWhenOnlyResourceQueueIsOccupied()
    {
        var status = CreateStatus([], [], 1, 600) with
        {
            Tribe = "Romans",
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Resource, "Cropland", 5, 600, "00:10:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var buildingItem = new QueueItem { TaskName = "upgrade_building_to_level" };

        var result = ConstructionQueueState.ResolveAvailabilityForItem(status, true, buildingItem);

        Assert.Equal(ConstructionQueueAvailability.Available, result);
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_AddsPersistedHumanizeExtraToLiveTimer()
    {
        var status = CreateStatus([], [], 1, 100) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 5, 100, "00:01:40"),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var item = new QueueItem
        {
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.QueueHumanizeExtraSeconds] = "50",
            },
        };

        var result = ConstructionQueueState.ResolveQueueFullRetryDelay(status, false, item);

        Assert.Equal(TimeSpan.FromSeconds(150), result);
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_KeepsFutureCombinedDeadlineWhenSlotIsFree()
    {
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        var status = CreateStatus([], [], 0, null) with
        {
            ActiveConstructions = [],
            ActiveConstructionsFromOverview = true,
        };
        var item = new QueueItem
        {
            NextAttemptAt = now.AddSeconds(45),
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.QueueHumanizeExtraSeconds] = "50",
            },
        };

        var result = ConstructionQueueState.ResolveQueueFullRetryDelay(status, true, item, now);

        Assert.Equal(TimeSpan.FromSeconds(45), result);
    }

    [Fact]
    public void ResolveQueueFullRetryDelay_ReleasesAfterCombinedDeadlineHasElapsed()
    {
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        var status = CreateStatus([], [], 0, null) with
        {
            ActiveConstructions = [],
            ActiveConstructionsFromOverview = true,
        };
        var item = new QueueItem
        {
            NextAttemptAt = now.AddSeconds(-1),
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.QueueHumanizeExtraSeconds] = "50",
            },
        };

        var result = ConstructionQueueState.ResolveQueueFullRetryDelay(status, true, item, now);

        Assert.Equal(TimeSpan.Zero, result);
    }

    private static VillageStatus CreateStatus(
        IReadOnlyList<Building> buildings,
        IReadOnlyList<BuildQueueItem> buildQueue,
        int activeBuildCount,
        int? remainingSeconds)
    {
        return new VillageStatus(
            ActiveVillage: "Village A",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: buildQueue,
            Tribe: "Romans",
            VillageCount: 1,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remainingSeconds,
            BuildQueueRemainingText: remainingSeconds?.ToString() ?? string.Empty);
    }

    private static QueueItem CreateDeferredQueueFullItem(string taskName)
    {
        return new QueueItem
        {
            TaskName = taskName,
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
            },
        };
    }
}
