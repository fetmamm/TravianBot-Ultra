using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Farming operations exposed by <see cref="TravianClient"/>: farm-list reads,
/// sends, list creation, and Official Travco-based farm additions.
/// </summary>
public interface IFarmingClient
{
    Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default);

    Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default);

    Task<int> SendAllFarmListsNowAsync(CancellationToken cancellationToken = default);

    // Sends only the toggled/selected farm lists (by name and/or stable lid), one at a time with per-list
    // confirmation and "Send farmlists" pacing. Returns exact attempted and confirmed list identities so
    // independent per-list schedules are advanced only for successful dispatches.
    Task<FarmListSendBatchResult> SendSelectedFarmListsNowAsync(
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyCollection<string> selectedIds,
        CancellationToken cancellationToken = default);

    // Sends every farm list in one action by clicking Travian's own "Start all farm lists" button. Returns
    // how many lists had an enabled Start button when clicked.
    Task<int> SendAllFarmListsViaStartAllButtonAsync(CancellationToken cancellationToken = default);

    Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(
        bool includeUnoccupiedOasis,
        CancellationToken cancellationToken = default);

    Task<FarmListLossDeactivationResult> HandleFarmListLossTargetsAsync(
        FarmListLossHandlingRequest request,
        CancellationToken cancellationToken = default);

    Task<FarmListCreateBatchResult> CreateFarmListsAsync(
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress>? progress = null,
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

}
