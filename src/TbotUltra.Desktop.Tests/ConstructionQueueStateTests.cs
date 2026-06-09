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
    [InlineData("Slot 20: upgrade to level 5 already queued and still in progress. queue_wait_seconds=3600")]
    [InlineData("Slot 20: upgrade toward max already queued and still in progress. queue_wait_seconds=3600")]
    public void IsQueueOccupancyDeferMessage_RecognizesQueueWaits(string message)
    {
        Assert.True(ConstructionQueueState.IsQueueOccupancyDeferMessage(message));
    }

    [Fact]
    public void IsQueueOccupancyDeferMessage_DoesNotClassifyResourceWait()
    {
        Assert.False(ConstructionQueueState.IsQueueOccupancyDeferMessage(
            "Building slot 20 needs resources. queue_wait_seconds=3600"));
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
    public void PreserveKnownConstructionState_KeepsActiveQueueForPartialRead()
    {
        var existing = CreateStatus(
            buildings: [new Building(19, "Main Building", 5, "g15", 15)],
            buildQueue: [new BuildQueueItem("Main Building level 6", "02:00:00")],
            activeBuildCount: 1,
            remainingSeconds: 7200);
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
            remainingSeconds: 7200);
        var full = CreateStatus(
            buildings: [new Building(19, "Main Building", 6, "g15", 15)],
            buildQueue: [],
            activeBuildCount: 0,
            remainingSeconds: null);

        var result = ConstructionQueueState.PreserveKnownConstructionState(full, existing);

        Assert.Equal(0, result.ActiveBuildCount);
        Assert.Null(result.BuildQueueRemainingSeconds);
        Assert.Empty(result.BuildQueue);
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
}
