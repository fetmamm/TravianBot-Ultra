using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

/// <summary>Runs manual farming actions through the existing farming-client seam.</summary>
internal sealed class ManualFarmingOperation(IFarmingClient client)
{
    public Task<FarmListLossDeactivationResult> HandleLossTargetsAsync(
        FarmListLossHandlingRequest request,
        CancellationToken cancellationToken)
        => client.HandleFarmListLossTargetsAsync(request, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(CancellationToken cancellationToken)
        => client.ReadFarmListsOverviewAsync(cancellationToken);

    public Task<int?> SendOneAsync(string farmListName, CancellationToken cancellationToken)
        => client.SendFarmListNowAsync(farmListName, cancellationToken);

    public Task<int> SendAllAsync(CancellationToken cancellationToken)
        => client.SendAllFarmListsNowAsync(cancellationToken);

    public async Task<int> SendSelectedAsync(
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyCollection<string> selectedIds,
        CancellationToken cancellationToken)
        => (await client.SendSelectedFarmListsNowAsync(selectedNames, selectedIds, cancellationToken)).SentCount;

    public Task<int> SendAllViaStartAllButtonAsync(CancellationToken cancellationToken)
        => client.SendAllFarmListsViaStartAllButtonAsync(cancellationToken);

    public Task<FarmAddBatchResult> AddFarmsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops,
        IProgress<FarmAddProgress>? progress,
        FarmTargetProtectionContext? protection,
        CancellationToken cancellationToken)
        => client.AddFarmsFromCoordinatesAsync(
            farmListName,
            troopType,
            troopCount,
            requestedCount,
            coordinates,
            useDefaultTroops,
            progress,
            protection,
            cancellationToken);

    public Task<FarmListCreateBatchResult> CreateListsAsync(
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress>? progress,
        CancellationToken cancellationToken)
        => client.CreateFarmListsAsync(request, progress, cancellationToken);
}
