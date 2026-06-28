using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Farming operations exposed by <see cref="TravianClient"/>: farm-list reads
/// and sends, list creation, and Natar farming. Seam introduced ahead of
/// extracting a dedicated farming collaborator (#7); <see cref="TravianClient"/>
/// implements it directly for now, so behavior is unchanged.
/// </summary>
public interface IFarmingClient
{
    Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default);

    Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default);

    Task<int> SendAllFarmListsNowAsync(CancellationToken cancellationToken = default);

    Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(
        bool includeUnoccupiedOasis,
        CancellationToken cancellationToken = default);

    Task<FarmListCreateBatchResult> CreateFarmListsAsync(
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FarmAddResult> AddSingleFarmFromNatarsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        CancellationToken cancellationToken = default);

    Task<FarmAddBatchResult> AddFarmsFromNatarsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IProgress<int>? addedProgress = null,
        CancellationToken cancellationToken = default);

    Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops = false,
        IProgress<FarmAddProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    Task<ManualFarmRunResult> StartManualFarmingFromNatarsAsync(
        string troopType,
        long troopCount,
        int troopVariancePercent,
        bool raidAttack,
        CancellationToken cancellationToken = default);
}
