using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public interface IDesktopBotService
{
    QueueItem Enqueue(string taskName, Dictionary<string, string>? payload, int priority, int maxRetries);
    QueueItem EnqueueRuntime(string taskName, string displayName, Dictionary<string, string>? payload, int priority, int maxRetries);
    bool RemoveQueueItem(Guid id);
    bool MoveQueueItemUp(Guid id);
    bool MoveQueueItemDown(Guid id);
    bool PauseQueueItem(Guid id);
    bool ResumeQueueItem(Guid id);
    bool RetryQueueItem(Guid id);
    void ClearQueue();
    IReadOnlyList<QueueItem> GetQueueItemsForDisplay();
    QueueItem? SelectNextQueueItem();
    bool MarkQueueItemRunning(Guid id);
    bool MarkQueueItemSucceeded(Guid id);
    bool MarkQueueItemCanceled(Guid id);
    bool MarkQueueItemDeferred(Guid id, TimeSpan delay);
    bool MarkQueueItemExecutionFailed(Guid id);

    Task ExecuteQueueItemAsync(BotOptions options, QueueItem item, Action<string> log, CancellationToken cancellationToken);
    Task ExecuteFallbackTasksAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);

    Task<bool> IsLoggedInAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<int?> SendFarmListNowAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken);
    Task<FarmAddResult> AddSingleFarmFromNatarsAsync(BotOptions options, string farmListName, string troopType, int troopCount, Action<string> log, CancellationToken cancellationToken);
    Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(BotOptions options, Action<string> log, bool forceRefresh, CancellationToken cancellationToken);
    Task<FarmAddBatchResult> AddFarmsFromNatarsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, Action<string> log, CancellationToken cancellationToken);
    Task<ManualFarmRunResult> StartManualFarmingFromNatarsAsync(BotOptions options, string troopType, int troopCount, int troopVariancePercent, bool raidAttack, Action<string> log, CancellationToken cancellationToken);
    Task<AccountSnapshot> AnalyzeProfileAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task ExecuteLoginAsync(BotOptions options, Action<string> log, bool keepBrowserOpenAfterLogin, CancellationToken cancellationToken);
    Task ExecuteLogoutAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task MarkMessagesAsReadAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken);
    Task MarkReportsAsReadAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken);
    Task ExecuteOnceAsync(BotOptions options, Action<string> log, IEnumerable<string>? tasksOverride, CancellationToken cancellationToken);
    Task<VillageStatus> ReadVillageStatusAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken);
    Task<VillageStatus> ReadVillageResourceStatusAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken);
    Task NavigateToVillageResourceFieldsAsync(BotOptions options, Action<string> log, string? villageName, string? villageUrl, CancellationToken cancellationToken);
    Task<InboxStatus> ReadInboxStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<int?> RefreshAdventureCountAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    Task<HeroAttributeSnapshot> ReadHeroAttributesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken);
    bool ConsumeBrowserClosedByUserSignal();
    Task ShutdownAsync(Action<string> log);
}
