using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageStatusReactionCoordinatorTests
{
    [Fact]
    public async Task RunAsync_PerformsTheVillageReactionInAuthoritativeOrder()
    {
        var port = new RecordingPort();
        var coordinator = new VillageStatusReactionCoordinator<string, int>();

        var result = await coordinator.RunAsync(
            "village-a",
            refreshInbox: true,
            inboxStatusChecked: false,
            port,
            CancellationToken.None);

        Assert.True(result.ShouldContinue);
        Assert.True(result.InboxStatusChecked);
        Assert.Equal(
            ["read", "publish:1", "inbox", "collect:1", "optional:2", "waits:3", "runtime", "execute:4"],
            port.Calls);
    }

    [Fact]
    public async Task RunAsync_DoesNotRepeatAnInboxReadAlreadyCompletedByTheRound()
    {
        var port = new RecordingPort();
        var coordinator = new VillageStatusReactionCoordinator<string, int>();

        var result = await coordinator.RunAsync(
            "village-a",
            refreshInbox: true,
            inboxStatusChecked: true,
            port,
            CancellationToken.None);

        Assert.False(result.InboxStatusChecked);
        Assert.DoesNotContain("inbox", port.Calls);
    }

    [Fact]
    public async Task RunAsync_StopsBeforeOptionalReadsWhenRewardCollectionBlocks()
    {
        var port = new RecordingPort(collectionShouldContinue: false);
        var coordinator = new VillageStatusReactionCoordinator<string, int>();

        var result = await coordinator.RunAsync(
            "village-a",
            refreshInbox: false,
            inboxStatusChecked: false,
            port,
            CancellationToken.None);

        Assert.False(result.ShouldContinue);
        Assert.Equal(["read", "publish:1", "collect:1"], port.Calls);
    }

    [Fact]
    public async Task RunAsync_ManualSleepAfterCurrentAction_SkipsFurtherVillageWork()
    {
        var port = new RecordingPort(requestStopAfterCollection: true);
        var coordinator = new VillageStatusReactionCoordinator<string, int>();

        var result = await coordinator.RunAsync(
            "village-a", refreshInbox: false, inboxStatusChecked: false, port, CancellationToken.None);

        Assert.False(result.ShouldContinue);
        Assert.Equal(["read", "publish:1", "collect:1"], port.Calls);
    }

    private sealed class RecordingPort(
        bool collectionShouldContinue = true,
        bool requestStopAfterCollection = false)
        : IVillageStatusReactionPort<string, int>
    {
        internal List<string> Calls { get; } = [];
        public bool StopRequested { get; private set; }

        public ValueTask<int> ReadBaseStatusAsync(string village, CancellationToken cancellationToken)
        {
            Calls.Add("read");
            return ValueTask.FromResult(1);
        }

        public ValueTask PublishBaseStatusAsync(
            string village,
            int status,
            CancellationToken cancellationToken)
        {
            Calls.Add($"publish:{status}");
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshInboxAsync(CancellationToken cancellationToken)
        {
            Calls.Add("inbox");
            return ValueTask.CompletedTask;
        }

        public ValueTask<VillageStatusCollectionResult<int>> CollectRewardsAsync(
            string village,
            int status,
            CancellationToken cancellationToken)
        {
            Calls.Add($"collect:{status}");
            StopRequested = requestStopAfterCollection;
            return ValueTask.FromResult(new VillageStatusCollectionResult<int>(2, collectionShouldContinue, 4));
        }

        public ValueTask<int> RefreshOptionalStatusesAsync(
            string village,
            int status,
            CancellationToken cancellationToken)
        {
            Calls.Add($"optional:{status}");
            return ValueTask.FromResult(3);
        }

        public ValueTask RefreshDeferredWaitsAsync(int status, CancellationToken cancellationToken)
        {
            Calls.Add($"waits:{status}");
            return ValueTask.CompletedTask;
        }

        public ValueTask ReconcileRuntimeItemsAsync(string village, CancellationToken cancellationToken)
        {
            Calls.Add("runtime");
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ExecuteReadyTasksAsync(
            string village,
            int collectionAttempts,
            CancellationToken cancellationToken)
        {
            Calls.Add($"execute:{collectionAttempts}");
            return ValueTask.FromResult(true);
        }
    }
}
