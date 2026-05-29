using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed class DesktopBotService : IDesktopBotService
{
    private readonly BotTaskRunner _taskRunner;
    private readonly IQueueStore _queueStore;
    private readonly IQueueScheduler _queueScheduler;
    private readonly QueueExecutor _queueExecutor;

    public DesktopBotService(BotTaskRunner taskRunner, IQueueStore queueStore, IQueueScheduler queueScheduler, QueueExecutor queueExecutor)
    {
        _taskRunner = taskRunner;
        _queueStore = queueStore;
        _queueScheduler = queueScheduler;
        _queueExecutor = queueExecutor;
    }

    public QueueItem Enqueue(string taskName, Dictionary<string, string>? payload, int priority, int maxRetries)
    {
        return _queueStore.Add(taskName, payload, priority, maxRetries);
    }

    public QueueItem EnqueueRuntime(string taskName, string displayName, Dictionary<string, string>? payload, int priority, int maxRetries)
    {
        return _queueStore.AddRuntime(taskName, displayName, payload, priority, maxRetries);
    }

    public bool RemoveQueueItem(Guid id) => _queueStore.Remove(id);
    public bool MoveQueueItemUp(Guid id) => _queueStore.MoveUp(id);
    public bool MoveQueueItemDown(Guid id) => _queueStore.MoveDown(id);
    public bool PauseQueueItem(Guid id) => _queueStore.Pause(id);
    public bool ResumeQueueItem(Guid id) => _queueStore.Resume(id);
    public bool RetryQueueItem(Guid id) => _queueStore.Retry(id);
    public void ClearQueue() => _queueStore.Clear();
    public IReadOnlyList<QueueItem> GetQueueItemsForDisplay() => _queueScheduler.OrderForDisplay(_queueStore.GetAll());
    public QueueItem? SelectNextQueueItem() => _queueScheduler.SelectNext(_queueStore.GetAll());
    public bool MarkQueueItemRunning(Guid id) => _queueStore.MarkRunning(id);
    public bool MarkQueueItemSucceeded(Guid id) => _queueStore.MarkSucceeded(id);
    public bool MarkQueueItemCanceled(Guid id) => _queueStore.MarkCanceled(id);
    public bool MarkQueueItemDeferred(Guid id, TimeSpan delay) => _queueStore.MarkDeferred(id, delay);
    public bool UpdateDeferredQueueItem(Guid id, Dictionary<string, string>? payload, TimeSpan? delay = null) => _queueStore.UpdateDeferred(id, payload, delay);
    public bool MarkQueueItemExecutionFailed(Guid id) => _queueStore.MarkExecutionFailed(id);
    public int ResetOrphanedRunningQueueItems() => _queueStore.ResetOrphanedRunningItems();

    public Task ExecuteQueueItemAsync(BotOptions options, QueueItem item, Action<string> log, CancellationToken cancellationToken)
    {
        return _queueExecutor.ExecuteAsync(options, item, log, cancellationToken);
    }

    public Task ExecuteFallbackTasksAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ExecuteOnceAsync(options, log, options.LoopTasks, null, cancellationToken);
    }

    public Task<bool> IsLoggedInAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.IsLoggedInAsync(options, log, null, cancellationToken);
    }

    public Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadAndPersistGoldClubStatusAsync(options, log, null, cancellationToken);
    }

    public Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadFarmListsOverviewAsync(options, log, null, cancellationToken);
    }

    public Task<int?> SendFarmListNowAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.SendFarmListNowAsync(options, farmListName, log, null, cancellationToken);
    }

    public Task<FarmAddResult> AddSingleFarmFromNatarsAsync(BotOptions options, string farmListName, string troopType, int troopCount, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.AddSingleFarmFromNatarsAsync(options, farmListName, troopType, troopCount, log, null, cancellationToken);
    }

    public Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(BotOptions options, Action<string> log, bool forceRefresh, CancellationToken cancellationToken)
    {
        return _taskRunner.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, log, forceRefresh, null, cancellationToken);
    }

    public Task<FarmAddBatchResult> AddFarmsFromNatarsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.AddFarmsFromNatarsAsync(options, farmListName, troopType, troopCount, requestedCount, log, null, cancellationToken);
    }

    public Task<ManualFarmRunResult> StartManualFarmingFromNatarsAsync(BotOptions options, string troopType, int troopCount, int troopVariancePercent, bool raidAttack, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.StartManualFarmingFromNatarsAsync(options, troopType, troopCount, troopVariancePercent, raidAttack, log, null, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadAvailableTroopsForCatapultWavesAsync(options, log, null, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(BotOptions options, Action<string> log, bool forceRefresh, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadAvailableTroopsForCatapultWavesAsync(options, log, forceRefresh, null, cancellationToken);
    }

    public Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(BotOptions options, Action<string> log, bool forceRefresh, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCatapultWaveSetupInfoAsync(options, log, forceRefresh, null, cancellationToken);
    }

    public Task<CatapultWaveRunResult> StartCatapultWavesAsync(BotOptions options, CatapultWaveRequest request, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.StartCatapultWavesAsync(options, request, log, null, cancellationToken);
    }

public Task ExecuteLoginAsync(BotOptions options, Action<string> log, bool keepBrowserOpenAfterLogin, CancellationToken cancellationToken)
    {
        return _taskRunner.ExecuteLoginAsync(options, log, null, cancellationToken, keepBrowserOpenAfterLogin);
    }

    public Task<PostLoginSnapshot> LoadPostLoginSnapshotAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.LoadPostLoginSnapshotAsync(options, log, null, cancellationToken);
    }

    public Task ExecuteLogoutAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ExecuteLogoutAsync(options, log, null, cancellationToken);
    }

    public Task MarkMessagesAsReadAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken)
    {
        return _taskRunner.MarkMessagesAsReadAsync(
            options,
            log,
            villageName: villageName,
            villageUrl: villageUrl,
            accountName: null,
            cancellationToken: cancellationToken);
    }

    public Task MarkReportsAsReadAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken)
    {
        return _taskRunner.MarkReportsAsReadAsync(
            options,
            log,
            villageName: villageName,
            villageUrl: villageUrl,
            accountName: null,
            cancellationToken: cancellationToken);
    }

    public Task ExecuteOnceAsync(BotOptions options, Action<string> log, IEnumerable<string>? tasksOverride, CancellationToken cancellationToken)
    {
        return _taskRunner.ExecuteOnceAsync(options, log, tasksOverride, null, cancellationToken);
    }

    public Task<VillageStatus> ReadBuildingsStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadBuildingsStatusAsync(options, log, null, cancellationToken);
    }

    public Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadTroopTrainingQueuesAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? knownBuildings, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadTroopTrainingQueuesAsync(options, log, knownBuildings, null, cancellationToken);
    }

    public Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? knownBuildings, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadBreweryCelebrationStatusAsync(options, log, knownBuildings, null, cancellationToken);
    }

    public Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? knownBuildings, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadSmithyUpgradeStatusAsync(options, log, knownBuildings, null, cancellationToken);
    }

    public Task<string> RunBreweryCelebrationAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunBreweryCelebrationAsync(options, log, null, cancellationToken);
    }

    public Task<string> RunNpcTradeForBuildingTestAsync(BotOptions options, Action<string> log, TroopTrainingBuildingType buildingType, CancellationToken cancellationToken)
    {
        return _taskRunner.RunNpcTradeForBuildingTestAsync(options, log, buildingType, null, cancellationToken);
    }

    public Task<string> RunNpcTradeForCurrentBuildingPageTestAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunNpcTradeForCurrentBuildingPageTestAsync(options, log, null, cancellationToken);
    }

    public Task<string> ReadSmithyQueueFromCurrentPageTestAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadSmithyQueueFromCurrentPageTestAsync(options, log, null, cancellationToken);
    }

    public Task<string> RunReinforcementsTestAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunReinforcementsTestAsync(options, log, null, cancellationToken);
    }

    public Task<VillageStatus> ReadVillageStatusAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadVillageStatusAsync(
            options,
            log,
            villageName: villageName,
            villageUrl: villageUrl,
            accountName: null,
            cancellationToken: cancellationToken);
    }

    public Task<VillageStatus> ReadVillageResourceStatusAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken, bool currentPageOnly = false)
    {
        return _taskRunner.ReadVillageResourceStatusAsync(
            options,
            log,
            villageName: villageName,
            villageUrl: villageUrl,
            currentPageOnly: currentPageOnly,
            accountName: null,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<VillageStatus>> ReadAllVillageResourceStatusesAsync(BotOptions options, Action<string> log, string? returnVillageName, string? returnVillageUrl, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadAllVillageResourceStatusesAsync(
            options,
            log,
            returnVillageName: returnVillageName,
            returnVillageUrl: returnVillageUrl,
            accountName: null,
            cancellationToken: cancellationToken);
    }

    public Task<VillageStatus> ReadCurrentPageResourceStatusQuickAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCurrentPageResourceStatusQuickAsync(options, log, null, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionPerHourAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCurrentPageResourceProductionPerHourAsync(options, log, null, cancellationToken);
    }

    public Task NavigateToVillageResourceFieldsAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken)
    {
        return _taskRunner.NavigateToVillageResourceFieldsAsync(
            options,
            log,
            villageName: villageName,
            villageUrl: villageUrl,
            accountName: null,
            cancellationToken: cancellationToken);
    }

    public Task<InboxStatus> ReadInboxStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadInboxStatusAsync(options, log, null, cancellationToken);
    }

    public Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.SendHeroOnAdventureAsync(options, log, null, cancellationToken);
    }

    public Task<int?> RefreshAdventureCountAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RefreshAdventureCountAsync(options, log, null, cancellationToken);
    }

    public Task<HeroAttributeSnapshot> ReadHeroAttributesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadHeroAttributesAsync(options, log, null, cancellationToken);
    }

    public bool ConsumeBrowserClosedByUserSignal()
    {
        return _taskRunner.ConsumeBrowserClosedByUserSignal();
    }

    public Task ShutdownAsync(Action<string> log)
    {
        return _taskRunner.ShutdownAsync(log);
    }
}
