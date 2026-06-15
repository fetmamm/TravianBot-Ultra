using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionQueueSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectNext_AvailableQueueValidatesFutureQueueFullItemImmediately()
    {
        var blocker = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonQueueFull);

        var result = ConstructionQueueSelector.SelectNext(
            [blocker],
            Now,
            ConstructionQueueAvailability.Available);

        Assert.Same(blocker, result.Item);
        Assert.True(result.ForcedLiveValidation);
        Assert.Null(result.QueueFullBlocker);
    }

    [Fact]
    public void SelectNext_FullQueueBlocksLaterConstruction()
    {
        var blocker = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonQueueFull);
        var later = CreateReadyItem();

        var result = ConstructionQueueSelector.SelectNext(
            [blocker, later],
            Now,
            ConstructionQueueAvailability.Full);

        Assert.Null(result.Item);
        Assert.Same(blocker, result.QueueFullBlocker);
    }

    [Fact]
    public void SelectNext_ResourceWaitPreservesConstructionOrder()
    {
        var resourceWait = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonResources);
        var later = CreateReadyItem();

        var result = ConstructionQueueSelector.SelectNext(
            [resourceWait, later],
            Now,
            ConstructionQueueAvailability.Available);

        Assert.Null(result.Item);
        Assert.Null(result.QueueFullBlocker);
        Assert.Contains("holding queue order", result.SkipReason);
    }

    [Fact]
    public void SelectNext_InProgressTargetAllowsLaterConstruction()
    {
        var inProgress = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonInProgress);
        var later = CreateReadyItem();

        var result = ConstructionQueueSelector.SelectNext(
            [inProgress, later],
            Now,
            ConstructionQueueAvailability.Available);

        Assert.Same(later, result.Item);
        Assert.False(result.ForcedLiveValidation);
    }

    [Fact]
    public void SelectNext_StorageCapacityDependencyAllowsLaterConstruction()
    {
        var capacityWait = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonStorageCapacity);
        var dependency = CreateReadyItem();

        var result = ConstructionQueueSelector.SelectNext(
            [capacityWait, dependency],
            Now,
            ConstructionQueueAvailability.Available);

        Assert.Same(dependency, result.Item);
    }

    [Fact]
    public void SelectNext_UnknownQueueValidatesOnlyLegacyQueueFullImmediately()
    {
        var legacy = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonQueueFull);
        var current = CreateDeferredItem(BotOptionPayloadKeys.UpgradeDeferReasonQueueFull);
        current.Payload[BotOptionPayloadKeys.UpgradeDeferClassificationVersion] =
            ConstructionQueueState.CurrentDeferClassificationVersion;

        var legacyResult = ConstructionQueueSelector.SelectNext(
            [legacy],
            Now,
            ConstructionQueueAvailability.Unknown);
        var currentResult = ConstructionQueueSelector.SelectNext(
            [current],
            Now,
            ConstructionQueueAvailability.Unknown);

        Assert.Same(legacy, legacyResult.Item);
        Assert.True(legacyResult.ForcedLiveValidation);
        Assert.Null(currentResult.Item);
        Assert.Same(current, currentResult.QueueFullBlocker);
    }

    private static QueueItem CreateDeferredItem(string reason)
    {
        return new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Status = QueueStatus.Pending,
            NextAttemptAt = Now.AddMinutes(15),
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.UpgradeDeferReason] = reason,
            },
        };
    }

    private static QueueItem CreateReadyItem()
    {
        return new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Status = QueueStatus.Pending,
            NextAttemptAt = Now,
        };
    }
}
