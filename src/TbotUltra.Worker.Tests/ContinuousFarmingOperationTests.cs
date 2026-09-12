using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ContinuousFarmingOperationTests
{
    [Fact]
    public async Task ExecuteAsync_SelectedReadyLists_HandlesLossesThenSendsAndReturnsSnapshot()
    {
        var client = new FakeFarmingClient(
            [new FarmListOverview("Mercs", 3, 3, 0, "42")],
            [new FarmListOverview("Mercs", 3, 3, 90, "42")]);
        var operation = new ContinuousFarmingOperation(client);
        var logs = new List<string>();

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeListPerList,
                ["Mercs"],
                ["42"],
                600,
                true,
                new FarmListLossHandlingRequest(false, false, "", "", "")),
            logs.Add,
            CancellationToken.None);

        Assert.Equal(["read", "loss", "send-selected", "read"], client.Calls);
        Assert.True(result.ScheduleNextRound);
        Assert.Equal(600, result.WaitSeconds);
        Assert.Equal(TaskWaitReasons.WorkQueued, result.WaitReasonCode);
        Assert.Equal("Mercs", Assert.Single(result.Snapshot!).Name);
        Assert.Equal("42", Assert.Single(result.SentLists!).ListId);
        Assert.NotNull(result.LossHandlingResult);
        Assert.Contains(logs, line => line.Contains("1 list(s) dispatched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_SelectedListWithFutureDeadline_DefersUntilItsOwnDeadline()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeFarmingClient([new FarmListOverview("Mercs", 3, 3, 0, "42")]);
        var operation = new ContinuousFarmingOperation(client);

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeListPerList,
                ["Mercs"],
                ["42"],
                600,
                false,
                null,
                NextSendAtUtcByKey: new Dictionary<string, DateTimeOffset?>
                {
                    ["lid:42"] = now.AddMinutes(5),
                },
                NowUtc: now),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["read"], client.Calls);
        Assert.Equal(300, result.WaitSeconds);
        Assert.Equal("No toggled farm list is due.", result.WaitMessage);
    }

    [Fact]
    public async Task ExecuteAsync_MixedDeadlines_SendsOnlyDueList()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var overview = new[]
        {
            new FarmListOverview("Near", 3, 3, 0, "1"),
            new FarmListOverview("Far", 3, 3, 0, "2"),
        };
        var client = new FakeFarmingClient(overview);
        var operation = new ContinuousFarmingOperation(client);

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeListPerList,
                ["Near", "Far"],
                ["1", "2"],
                600,
                false,
                null,
                NextSendAtUtcByKey: new Dictionary<string, DateTimeOffset?>
                {
                    ["lid:1"] = now,
                    ["lid:2"] = now.AddMinutes(10),
                },
                NowUtc: now),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["1"], client.SelectedIds);
        Assert.Equal("1", Assert.Single(result.SentLists!).ListId);
    }

    [Fact]
    public async Task ExecuteAsync_SelectedListsMissing_DefersWithoutBrowserMutation()
    {
        var client = new FakeFarmingClient([new FarmListOverview("Different", 1, 1, 0, "7")]);
        var operation = new ContinuousFarmingOperation(client);

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeListPerList,
                ["Mercs"],
                [],
                600,
                false,
                null),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["read"], client.Calls);
        Assert.False(result.ScheduleNextRound);
        Assert.Equal("Selected farm lists were not found on the farm page.", result.WaitMessage);
        Assert.Equal(600, result.WaitSeconds);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task ExecuteAsync_AllAtOnce_PreservesLossHandlingThenSendOrder()
    {
        var client = new FakeFarmingClient(
            [new FarmListOverview("Mercs", 3, 3, 0, "42")]);
        var operation = new ContinuousFarmingOperation(client);

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeAllAtOnce,
                [],
                [],
                600,
                true,
                new FarmListLossHandlingRequest(false, false, "", "", "")),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["loss", "start-all", "read"], client.Calls);
        Assert.True(result.ScheduleNextRound);
        Assert.Equal("Continuous farming cooldown active.", result.WaitMessage);
    }

    [Fact]
    public async Task ExecuteAsync_SharedSchedule_SendsEnabledListsAndIgnoresIndividualDeadlines()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var overview = new[]
        {
            new FarmListOverview("Enabled", 3, 3, 0, "1"),
            new FarmListOverview("Not selected", 3, 3, 0, "2"),
        };
        var client = new FakeFarmingClient(overview);
        var operation = new ContinuousFarmingOperation(client);

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeSharedSchedule,
                ["Enabled"],
                ["1"],
                600,
                false,
                null,
                NextSendAtUtcByKey: new Dictionary<string, DateTimeOffset?>
                {
                    ["lid:1"] = now.AddHours(1),
                },
                NowUtc: now),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["read", "send-selected", "read"], client.Calls);
        Assert.Equal(["1"], client.SelectedIds);
        Assert.True(result.ScheduleNextRound);
        Assert.Equal(600, result.WaitSeconds);
    }

    [Fact]
    public async Task ExecuteAsync_AllAtOnce_IgnoresPerListDeadlines()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeFarmingClient([new FarmListOverview("Mercs", 3, 3, 0, "42")]);
        var operation = new ContinuousFarmingOperation(client);

        await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeAllAtOnce,
                [],
                [],
                600,
                false,
                null,
                NextSendAtUtcByKey: new Dictionary<string, DateTimeOffset?>
                {
                    ["lid:42"] = now.AddHours(1),
                },
                NowUtc: now),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["start-all", "read"], client.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesRedAndYellowWithSeparateRequestsBeforeSending()
    {
        var client = new FakeFarmingClient([new FarmListOverview("Mercs", 3, 3, 0, "42")]);
        var operation = new ContinuousFarmingOperation(client);
        var red = new FarmListLossHandlingRequest(false, true, "red", "Red farms", "Red farms", LossColors: FarmListLossColors.Red);
        var yellow = new FarmListLossHandlingRequest(false, true, "yellow", "Yellow farms", "Yellow farms", LossColors: FarmListLossColors.Yellow);

        var result = await operation.ExecuteAsync(
            new ContinuousFarmingDispatchRequest(
                FarmingDefaults.SendModeAllAtOnce,
                [],
                [],
                600,
                true,
                null,
                [red, yellow]),
            _ => { },
            CancellationToken.None);

        Assert.Equal(["loss", "loss", "start-all", "read"], client.Calls);
        Assert.Equal([FarmListLossColors.Red, FarmListLossColors.Yellow], client.LossRequests.Select(request => request.LossColors));
        Assert.Equal(2, result.LossHandlingResults!.Count);
    }

    private sealed class FakeFarmingClient(
        IReadOnlyList<FarmListOverview> initialOverview,
        IReadOnlyList<FarmListOverview>? refreshedOverview = null) : IFarmingClient
    {
        private readonly Queue<IReadOnlyList<FarmListOverview>> _overviews = new([initialOverview, refreshedOverview ?? initialOverview]);

        public List<string> Calls { get; } = [];
        public List<FarmListLossHandlingRequest> LossRequests { get; } = [];
        public IReadOnlyCollection<string> SelectedIds { get; private set; } = [];

        public Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("read");
            return Task.FromResult(_overviews.Count > 1 ? _overviews.Dequeue() : _overviews.Peek());
        }

        public Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> SendAllFarmListsNowAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("send-all");
            return Task.FromResult(1);
        }

        public Task<FarmListSendBatchResult> SendSelectedFarmListsNowAsync(IReadOnlyCollection<string> selectedNames, IReadOnlyCollection<string> selectedIds, CancellationToken cancellationToken = default)
        {
            Calls.Add("send-selected");
            SelectedIds = selectedIds.ToList();
            var sent = initialOverview
                .Where(item => selectedIds.Count > 0
                    ? item.ListId is not null && selectedIds.Contains(item.ListId)
                    : selectedNames.Contains(item.Name))
                .Select(item => new FarmListSendEntry(item.Name, item.ListId))
                .ToList();
            return Task.FromResult(new FarmListSendBatchResult(sent, sent));
        }

        public Task<int> SendAllFarmListsViaStartAllButtonAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start-all");
            return Task.FromResult(1);
        }
        public Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(bool includeUnoccupiedOasis, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FarmListLossDeactivationResult> HandleFarmListLossTargetsAsync(FarmListLossHandlingRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("loss");
            LossRequests.Add(request);
            return Task.FromResult(new FarmListLossDeactivationResult(2, 1, 0, LossColors: request.LossColors));
        }

        public Task<FarmListCreateBatchResult> CreateFarmListsAsync(FarmListCreateRequest request, IProgress<FarmListCreateProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops = false, IProgress<FarmAddProgress>? progress = null, FarmTargetProtectionContext? protection = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
