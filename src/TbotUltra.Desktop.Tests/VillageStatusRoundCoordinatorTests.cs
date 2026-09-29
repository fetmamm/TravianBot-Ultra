using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageStatusRoundCoordinatorTests
{
    [Fact]
    public async Task RunAsync_PreparesOnceVisitsRandomizedOrderAndDelaysOnlyBetweenVillages()
    {
        var coordinator = new VillageStatusRoundCoordinator((_, _) => 0);
        var port = new RecordingPort();
        var villages = new[]
        {
            new VillageStatusRoundVillage("a", "A", null),
            new VillageStatusRoundVillage("b", "B", null),
            new VillageStatusRoundVillage("c", "C", null),
        };

        var result = await coordinator.RunAsync(villages, port, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(3, result.VisitedVillages);
        Assert.Equal(1, port.PrepareCount);
        Assert.Equal(2, port.DelayCount);
        Assert.Equal(["B", "C", "A"], port.VisitedNames);
        Assert.Equal([(1, 3), (2, 3), (3, 3)], port.Progress);
    }

    [Fact]
    public async Task RunAsync_CarriesInboxCheckAcrossTheRound()
    {
        var coordinator = new VillageStatusRoundCoordinator((_, max) => max - 1);
        var port = new RecordingPort(markInboxCheckedOnFirstVisit: true);

        await coordinator.RunAsync(
            [new("a", "A", null), new("b", "B", null)],
            port,
            CancellationToken.None);

        Assert.Equal([false, true], port.InboxWasAlreadyChecked);
    }

    [Fact]
    public async Task RunAsync_StopsAtTheFirstVisitThatBlocksContinuation()
    {
        var coordinator = new VillageStatusRoundCoordinator((_, max) => max - 1);
        var port = new RecordingPort(stopAfterVisit: 1);

        var result = await coordinator.RunAsync(
            [new("a", "A", null), new("b", "B", null)],
            port,
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(1, result.VisitedVillages);
        Assert.Single(port.VisitedNames);
        Assert.Equal(0, port.DelayCount);
    }

    private sealed class RecordingPort(
        bool markInboxCheckedOnFirstVisit = false,
        int? stopAfterVisit = null) : IVillageStatusRoundPort
    {
        internal int PrepareCount { get; private set; }

        internal int DelayCount { get; private set; }

        internal List<string> VisitedNames { get; } = [];

        internal List<bool> InboxWasAlreadyChecked { get; } = [];

        internal List<(int Current, int Total)> Progress { get; } = [];

        public void UpdateRoundActivity(int villageNumber, int villageCount) =>
            Progress.Add((villageNumber, villageCount));

        public ValueTask PrepareAsync(CancellationToken cancellationToken)
        {
            PrepareCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<VillageStatusRoundVisitResult> VisitAsync(
            VillageStatusRoundVillage village,
            int villageNumber,
            int villageCount,
            bool inboxStatusChecked,
            CancellationToken cancellationToken)
        {
            VisitedNames.Add(village.Name);
            InboxWasAlreadyChecked.Add(inboxStatusChecked);
            var shouldContinue = stopAfterVisit is null || villageNumber < stopAfterVisit.Value;
            return ValueTask.FromResult(new VillageStatusRoundVisitResult(
                shouldContinue,
                markInboxCheckedOnFirstVisit && villageNumber == 1));
        }

        public ValueTask DelayBeforeNextVillageAsync(CancellationToken cancellationToken)
        {
            DelayCount++;
            return ValueTask.CompletedTask;
        }
    }
}
