using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public interface IFarmListsBrowserAdapter
{
    Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<FarmAddBatchResult> AddFarmsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, FarmTargetProtectionContext? protection, Action<string> log, IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken);
    Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<FarmListCreateBatchResult> CreateListsAsync(BotOptions options, FarmListCreateRequest request, Action<string> log, IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken);
    Task<FarmListSingleSendResult> SendOneWithResultAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken);
    Task<FarmListSendBatchResult> SendSelectedWithResultsAsync(BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids, Action<string> log, CancellationToken cancellationToken);
}

internal sealed class OfficialFarmListsBrowserAdapter(BotTaskRunner taskRunner) : IFarmListsBrowserAdapter
{
    public Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => taskRunner.ReadAndPersistGoldClubStatusAsync(options, log, null, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => taskRunner.ReadFarmListsOverviewAsync(options, log, null, cancellationToken);

    public Task<FarmAddBatchResult> AddFarmsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, FarmTargetProtectionContext? protection, Action<string> log, IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken)
        => taskRunner.AddFarmsFromCoordinatesAsync(options, farmListName, troopType, troopCount, requestedCount, coordinates, useDefaultTroops, log, null, progress, protection, cancellationToken);

    public Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => taskRunner.ReadFarmTargetProtectionIdentityAsync(options, log, null, cancellationToken);

    public Task<FarmListCreateBatchResult> CreateListsAsync(BotOptions options, FarmListCreateRequest request, Action<string> log, IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken)
        => taskRunner.CreateFarmListsAsync(options, request, log, null, progress, cancellationToken);

    public Task<FarmListSingleSendResult> SendOneWithResultAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken)
        => taskRunner.SendFarmListNowWithResultAsync(options, farmListName, log, null, cancellationToken);

    public Task<FarmListSendBatchResult> SendSelectedWithResultsAsync(BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids, Action<string> log, CancellationToken cancellationToken)
        => taskRunner.SendSelectedFarmListsWithResultsNowAsync(options, names, ids, log, null, cancellationToken);
}
