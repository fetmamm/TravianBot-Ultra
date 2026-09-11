using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ManualFarmingOperationTests
{
    [Fact]
    public async Task EveryOperation_ForwardsPayloadCollectionsAndCancellationUnchanged()
    {
        var client = new RecordingFarmingClient();
        var operation = new ManualFarmingOperation(client);
        var lossRequest = new FarmListLossHandlingRequest(true, true, "42", "Losses", "Base");
        var createRequest = new FarmListCreateRequest(["A"], "Capital", "did:1", "Phalanx", 3);
        IReadOnlyList<FarmCoordinate> coordinates = [new(1, -2)];
        IReadOnlyCollection<string> names = ["A"];
        IReadOnlyCollection<string> ids = ["42"];
        using var cancellation = new CancellationTokenSource();

        Assert.Same(client.LossResult, await operation.HandleLossTargetsAsync(lossRequest, cancellation.Token));
        Assert.Same(client.Overview, await operation.ReadOverviewAsync(cancellation.Token));
        Assert.Equal(1, await operation.SendOneAsync("A", cancellation.Token));
        Assert.Equal(2, await operation.SendAllAsync(cancellation.Token));
        Assert.Equal(3, await operation.SendSelectedAsync(names, ids, cancellation.Token));
        Assert.Equal(4, await operation.SendAllViaStartAllButtonAsync(cancellation.Token));
        Assert.Same(client.AddResult, await operation.AddFarmsAsync("A", "Phalanx", 3, 5, coordinates, true, null, cancellation.Token));
        Assert.Same(client.CreateResult, await operation.CreateListsAsync(createRequest, null, cancellation.Token));

        Assert.Equal(["loss", "overview", "one", "all", "selected", "start-all", "add", "create"], client.Calls);
        Assert.Same(lossRequest, client.LossRequest);
        Assert.Same(names, client.SelectedNames);
        Assert.Same(ids, client.SelectedIds);
        Assert.Same(coordinates, client.Coordinates);
        Assert.Same(createRequest, client.CreateRequest);
        Assert.All(client.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }

    private sealed class RecordingFarmingClient : IFarmingClient
    {
        public FarmListLossDeactivationResult LossResult { get; } = new(1, 1, 0);
        public IReadOnlyList<FarmListOverview> Overview { get; } = [new("A", 1, 2, 30)];
        public FarmAddBatchResult AddResult { get; } = new("A", 5, 5, 3, 1, 1);
        public FarmListCreateBatchResult CreateResult { get; } = new(1, 1, ["A"]);
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public FarmListLossHandlingRequest? LossRequest { get; private set; }
        public IReadOnlyCollection<string>? SelectedNames { get; private set; }
        public IReadOnlyCollection<string>? SelectedIds { get; private set; }
        public IReadOnlyList<FarmCoordinate>? Coordinates { get; private set; }
        public FarmListCreateRequest? CreateRequest { get; private set; }

        public Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default) => Record("overview", cancellationToken, Overview);
        public Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default) => Record("one", cancellationToken, (int?)1);
        public Task<int> SendAllFarmListsNowAsync(CancellationToken cancellationToken = default) => Record("all", cancellationToken, 2);
        public Task<FarmListSendBatchResult> SendSelectedFarmListsNowAsync(IReadOnlyCollection<string> selectedNames, IReadOnlyCollection<string> selectedIds, CancellationToken cancellationToken = default)
        {
            SelectedNames = selectedNames;
            SelectedIds = selectedIds;
            return Record(
                "selected",
                cancellationToken,
                new FarmListSendBatchResult(
                    [new("A", "1"), new("B", "2"), new("C", "3")],
                    [new("A", "1"), new("B", "2"), new("C", "3")]));
        }
        public Task<int> SendAllFarmListsViaStartAllButtonAsync(CancellationToken cancellationToken = default) => Record("start-all", cancellationToken, 4);
        public Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(bool includeUnoccupiedOasis, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FarmListLossDeactivationResult> HandleFarmListLossTargetsAsync(FarmListLossHandlingRequest request, CancellationToken cancellationToken = default)
        {
            LossRequest = request;
            return Record("loss", cancellationToken, LossResult);
        }
        public Task<FarmListCreateBatchResult> CreateFarmListsAsync(FarmListCreateRequest request, IProgress<FarmListCreateProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CreateRequest = request;
            return Record("create", cancellationToken, CreateResult);
        }
        public Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops = false, IProgress<FarmAddProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Coordinates = coordinates;
            return Record("add", cancellationToken, AddResult);
        }
        private Task<T> Record<T>(string call, CancellationToken token, T result)
        {
            Calls.Add(call);
            CancellationTokens.Add(token);
            return Task.FromResult(result);
        }
    }
}
