using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public interface IHeroPanelClient
{
    Task<HeroAttributeSnapshot> ReadAttributesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<int?> ReadAdventureCountAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<int?> ReadHpAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<HeroInventoryResources> ReadInventoryAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
}

public interface IFarmingPanelClient
{
    Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<FarmAddBatchResult> AddFarmsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, FarmTargetProtectionContext? protection, Action<string> log, IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken);
    Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<FarmListCreateBatchResult> CreateListsAsync(BotOptions options, FarmListCreateRequest request, Action<string> log, IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken);
    Task<int?> SendOneAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken);
    Task<int> SendSelectedAsync(BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids, Action<string> log, CancellationToken cancellationToken);
    Task<int> SendAllAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
}

public interface IBuildingsPanelClient
{
    IReadOnlyList<QueueItem> GetQueueItems();
    IReadOnlyList<QueueItem> EnqueueBatch(IReadOnlyList<QueueItemCreateRequest> requests);
    QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries);
    bool Remove(Guid id);
    bool UpdatePending(Guid id, Dictionary<string, string> payload);
    bool ApplyPendingReconciliation(IReadOnlyList<Guid> removals, IReadOnlyList<QueuePayloadUpdate> updates);
}

public interface IQueuePanelClient
{
    IReadOnlyList<QueueItem> GetItems();
    bool Remove(Guid id);
    int RemoveMany(IReadOnlyCollection<Guid> ids);
    bool MoveUp(Guid id);
    bool MoveDown(Guid id);
    bool MoveToTop(Guid id);
    bool MoveToBottom(Guid id);
    bool ApplyOrder(IReadOnlyList<Guid> orderedIds);
    bool Pause(Guid id);
    bool Resume(Guid id);
    bool Retry(Guid id);
    QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries);
}

public interface ITroopTrainingPanelClient
{
    Task<VillageStatus> ReadBuildingsAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadQueuesAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken);
    Task<SmithyUpgradeStatus> ReadSmithyStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken);
    Task<BreweryCelebrationStatus> ReadBreweryStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken);
}

internal sealed class DesktopHeroPanelClient(IDesktopBotService botService) : IHeroPanelClient
{
    public Task<HeroAttributeSnapshot> ReadAttributesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.ReadHeroAttributesAsync(options, log, cancellationToken);

    public Task<int?> ReadAdventureCountAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.RefreshAdventureCountAsync(options, log, cancellationToken);

    public Task<int?> ReadHpAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.ReadHeroHpFromCurrentPageAsync(options, log, cancellationToken);

    public Task<HeroInventoryResources> ReadInventoryAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.RefreshHeroInventoryAsync(options, log, cancellationToken);
}

internal sealed class DesktopFarmingPanelClient(IDesktopBotService botService) : IFarmingPanelClient
{
    public Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.ReadAndPersistGoldClubStatusAsync(options, log, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.ReadFarmListsOverviewAsync(options, log, cancellationToken);

    public Task<FarmAddBatchResult> AddFarmsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, FarmTargetProtectionContext? protection, Action<string> log, IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken)
        => botService.AddFarmsFromCoordinatesAsync(options, farmListName, troopType, troopCount, requestedCount, coordinates, useDefaultTroops, protection, log, progress, cancellationToken);

    public Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.ReadFarmTargetProtectionIdentityAsync(options, log, cancellationToken);

    public Task<FarmListCreateBatchResult> CreateListsAsync(BotOptions options, FarmListCreateRequest request, Action<string> log, IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken)
        => botService.CreateFarmListsAsync(options, request, log, progress, cancellationToken);

    public Task<int?> SendOneAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken)
        => botService.SendFarmListNowAsync(options, farmListName, log, cancellationToken);

    public Task<int> SendSelectedAsync(BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids, Action<string> log, CancellationToken cancellationToken)
        => botService.SendSelectedFarmListsNowAsync(options, names, ids, log, cancellationToken);

    public Task<int> SendAllAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.SendAllFarmListsViaStartAllButtonAsync(options, log, cancellationToken);
}

internal sealed class DesktopBuildingsPanelClient(IDesktopBotService botService) : IBuildingsPanelClient
{
    public IReadOnlyList<QueueItem> GetQueueItems() => botService.GetQueueItemsForDisplay();
    public IReadOnlyList<QueueItem> EnqueueBatch(IReadOnlyList<QueueItemCreateRequest> requests) => botService.EnqueueBatch(requests);
    public QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries) => botService.Enqueue(taskName, payload, priority, maxRetries);
    public bool Remove(Guid id) => botService.RemoveQueueItem(id);
    public bool UpdatePending(Guid id, Dictionary<string, string> payload) => botService.UpdatePendingQueueItem(id, payload, priority: null);
    public bool ApplyPendingReconciliation(IReadOnlyList<Guid> removals, IReadOnlyList<QueuePayloadUpdate> updates) => botService.ApplyPendingQueueReconciliation(removals, updates);
}

internal sealed class DesktopQueuePanelClient(IDesktopBotService botService) : IQueuePanelClient
{
    public IReadOnlyList<QueueItem> GetItems() => botService.GetQueueItemsForDisplay();
    public bool Remove(Guid id) => botService.RemoveQueueItem(id);
    public int RemoveMany(IReadOnlyCollection<Guid> ids) => botService.RemoveQueueItems(ids);
    public bool MoveUp(Guid id) => botService.MoveQueueItemUp(id);
    public bool MoveDown(Guid id) => botService.MoveQueueItemDown(id);
    public bool MoveToTop(Guid id) => botService.MoveQueueItemToTop(id);
    public bool MoveToBottom(Guid id) => botService.MoveQueueItemToBottom(id);
    public bool ApplyOrder(IReadOnlyList<Guid> orderedIds) => botService.ApplyQueueOrder(orderedIds);
    public bool Pause(Guid id) => botService.PauseQueueItem(id);
    public bool Resume(Guid id) => botService.ResumeQueueItem(id);
    public bool Retry(Guid id) => botService.RetryQueueItem(id);
    public QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries) => botService.Enqueue(taskName, payload, priority, maxRetries);
}

internal sealed class DesktopTroopTrainingPanelClient(IDesktopBotService botService) : ITroopTrainingPanelClient
{
    public Task<VillageStatus> ReadBuildingsAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => botService.ReadBuildingsStatusAsync(options, log, cancellationToken);

    public Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadQueuesAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken)
        => botService.ReadTroopTrainingQueuesAsync(options, log, buildings, cancellationToken);

    public Task<SmithyUpgradeStatus> ReadSmithyStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken)
        => botService.ReadSmithyUpgradeStatusAsync(options, log, buildings, cancellationToken);

    public Task<BreweryCelebrationStatus> ReadBreweryStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken)
        => botService.ReadBreweryCelebrationStatusAsync(options, log, buildings, cancellationToken);
}
