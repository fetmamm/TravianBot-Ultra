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

    public IReadOnlyList<QueueItem> EnqueueBatch(IReadOnlyList<QueueItemCreateRequest> requests)
    {
        return _queueStore.AddBatch(requests);
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
    public bool UpdatePendingQueueItem(Guid id, Dictionary<string, string>? payload, int? priority, TimeSpan? delay = null) => _queueStore.UpdatePending(id, payload, priority, delay);
    public bool MarkQueueItemExecutionFailed(Guid id) => _queueStore.MarkExecutionFailed(id);
    public bool MarkQueueItemPermanentlyFailed(Guid id) => _queueStore.MarkPermanentlyFailed(id);
    public int ResetOrphanedRunningQueueItems() => _queueStore.ResetOrphanedRunningItems();

    public Task<BotTaskExecutionResult> ExecuteQueueItemAsync(BotOptions options, QueueItem item, Action<string> log, CancellationToken cancellationToken)
    {
        return _queueExecutor.ExecuteAsync(options, item, log, cancellationToken);
    }

    public Task ExecuteFallbackTasksAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ExecuteOnceAsync(options, log, options.LoopTasks, null, cancellationToken);
    }

    public Task<IReadOnlyList<MapOasisEntry>> ScanMapOasesAsync(
        BotOptions options,
        bool includeOccupied,
        IReadOnlyCollection<string> selectedTypes,
        Action<string> log,
        IProgress<MapOasisScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return _taskRunner.ScanMapOasesAsync(
            options,
            includeOccupied,
            selectedTypes,
            log,
            progress,
            null,
            cancellationToken);
    }

    public Task<bool> IsLoggedInAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.IsLoggedInAsync(options, log, null, cancellationToken);
    }

    public Task<string?> ReadCurrentLanguageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCurrentLanguageAsync(options, log, null, cancellationToken);
    }

    public Task EnsureExpectedLanguageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.EnsureExpectedLanguageAsync(options, log, null, cancellationToken);
    }

    public Task<string?> SetLanguageToEnglishAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.SetLanguageToEnglishAsync(options, log, null, cancellationToken);
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

    public Task<int> SendAllFarmListsNowAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.SendAllFarmListsNowAsync(options, log, null, cancellationToken);
    }

    public Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, Action<string> log, IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken)
    {
        return _taskRunner.AddFarmsFromCoordinatesAsync(options, farmListName, troopType, troopCount, requestedCount, coordinates, useDefaultTroops, log, null, progress, cancellationToken);
    }

    public Task<FarmListCreateBatchResult> CreateFarmListsAsync(BotOptions options, FarmListCreateRequest request, Action<string> log, IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken)
    {
        return _taskRunner.CreateFarmListsAsync(options, request, log, null, progress, cancellationToken);
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

    public Task<PostLoginSnapshot> ExecuteLoginAndLoadPostLoginSnapshotAsync(BotOptions options, Action<string> log, bool keepBrowserOpenAfterLogin, CancellationToken cancellationToken)
    {
        return _taskRunner.ExecuteLoginAndLoadPostLoginSnapshotAsync(options, log, null, cancellationToken, keepBrowserOpenAfterLogin);
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

    public Task<string> RunIncreaseAdventuresToHardAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunIncreaseAdventuresToHardAsync(options, log, null, cancellationToken);
    }

    public Task<string> RunReduceAdventuresTimeAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunReduceAdventuresTimeAsync(options, log, null, cancellationToken);
    }

    public Task<string> RunScanProductionBonusTimersAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunScanProductionBonusTimersAsync(options, log, null, cancellationToken);
    }

    public Task<string> RunActivateProductionBonusVideosAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunActivateProductionBonusVideosAsync(options, log, null, cancellationToken);
    }

    public Task<string> ReadSmithyQueueFromCurrentPageTestAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadSmithyQueueFromCurrentPageTestAsync(options, log, null, cancellationToken);
    }

    public Task<string> RunReinforcementsTestAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RunReinforcementsTestAsync(options, log, null, cancellationToken);
    }

    public Task<AccountSnapshot> ReadAccountSnapshotForScanAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadAccountSnapshotForScanAsync(options, log, null, cancellationToken);
    }

    public Task<VillageStatus> ReadVillageStatusWithSmithyAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadVillageStatusWithSmithyAsync(
            options,
            log,
            villageName,
            villageUrl,
            null,
            cancellationToken);
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

    public Task<VillageStatus> ReadCurrentPageStorageStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCurrentPageStorageStatusAsync(options, log, null, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionPerHourAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCurrentPageResourceProductionPerHourAsync(options, log, null, cancellationToken);
    }

    public Task<PageHtmlCapture> ReadCurrentPageHtmlAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadCurrentPageHtmlAsync(options, log, null, cancellationToken);
    }

    public Task<ReportPngResult> SaveReportScreenshotAsync(BotOptions options, string filePath, bool hideAttacker, bool hideDefender, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.SaveReportScreenshotAsync(options, filePath, hideAttacker, hideDefender, log, null, cancellationToken);
    }

    public Task<PageHtmlCapture> NavigateToPageAndReadHtmlAsync(BotOptions options, string pagePath, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.NavigateToPageAndReadHtmlAsync(options, pagePath, log, null, cancellationToken);
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

    public Task RefreshCurrentPageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RefreshCurrentPageAsync(options, log, accountName: null, cancellationToken: cancellationToken);
    }

    public Task<InboxStatus> ReadInboxStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadInboxStatusAsync(options, log, null, cancellationToken);
    }

    public Task<BulkMessageAnalyzeResult> AnalyzeBulkMessagePlayersAsync(BotOptions options, BulkMessageAnalyzeRequest request, Action<string> log, IProgress<BulkMessageProgress>? progress, CancellationToken cancellationToken)
    {
        return _taskRunner.AnalyzeBulkMessagePlayersAsync(options, request, log, progress, null, cancellationToken);
    }

    public Task<BulkMessageSendResult> SendBulkMessagesAsync(BotOptions options, BulkMessageRequest request, Action<string> log, IProgress<BulkMessageProgress>? progress, CancellationToken cancellationToken)
    {
        return _taskRunner.SendBulkMessagesAsync(options, request, log, progress, null, cancellationToken);
    }

    public void ClearBulkMessageSentCache(BotOptions options, Action<string> log)
    {
        _taskRunner.ClearBulkMessageSentCache(options, log, null);
    }

    public Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.SendHeroOnAdventureAsync(options, log, null, cancellationToken);
    }

    public Task<bool> CheckAndReviveDeadHeroAsync(BotOptions options, bool autoRevive, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.CheckAndReviveDeadHeroAsync(options, autoRevive, log, null, cancellationToken);
    }

    public Task<int?> RefreshAdventureCountAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.RefreshAdventureCountAsync(options, log, null, cancellationToken);
    }

    public Task<bool> HasHeroLevelUpIndicatorOnCurrentPageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.HasHeroLevelUpIndicatorOnCurrentPageAsync(options, log, null, cancellationToken);
    }

    public Task<bool> IsHeroRevivingOnCurrentPageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.IsHeroRevivingOnCurrentPageAsync(options, log, null, cancellationToken);
    }

    public Task<bool> HasClaimableTasksOnCurrentPageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.HasClaimableTasksOnCurrentPageAsync(options, log, null, cancellationToken);
    }

    public Task<bool> HasClaimableDailyQuestsOnCurrentPageAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.HasClaimableDailyQuestsOnCurrentPageAsync(options, log, null, cancellationToken);
    }

    public Task<HeroAttributeSnapshot> ReadHeroAttributesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadHeroAttributesAsync(options, log, null, cancellationToken);
    }

    public Task<HeroInventoryResources> RefreshHeroInventoryAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ReadHeroInventoryResourcesAsync(options, log, null, cancellationToken);
    }

    public Task OpenTravcoAndSearchAsync(BotOptions options, TravcoSearchRequest request, Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.OpenTravcoAndSearchAsync(options, request, log, cancellationToken);
    }

    public Task<TravcoScrapeResult> ScrapeTravcoPageAsync(Action<string> log, CancellationToken cancellationToken)
    {
        return _taskRunner.ScrapeTravcoPageAsync(log, cancellationToken);
    }

    public Task<TravcoScrapeResult> ScrapeAllTravcoPagesAsync(
        Action<string> log,
        IProgress<(int CurrentPage, int TotalPages)> progress,
        CancellationToken cancellationToken)
    {
        return _taskRunner.ScrapeAllTravcoPagesAsync(log, progress, cancellationToken);
    }

    public Task CloseTravcoTabAsync(Action<string> log)
    {
        return _taskRunner.CloseTravcoTabAsync(log);
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
