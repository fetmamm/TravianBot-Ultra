using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using Microsoft.Playwright;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed class BotTaskRunner
{

    private static readonly IReadOnlyDictionary<string, Func<TaskExecutionContext, Task>> TaskHandlers =
        new Dictionary<string, Func<TaskExecutionContext, Task>>(StringComparer.OrdinalIgnoreCase)
        {
            // Reads and logs the current village status, including villages, resources, buildings, and build queue.
            ["status"] = ExecuteStatusAsync,
            // Scans and reads the status of all villages in the account.
            ["scan_all_villages"] = ExecuteScanAllVillagesAsync,
            // Reads and logs a snapshot of the account, including tribe, active village, and village count.
            ["account_snapshot"] = ExecuteAccountSnapshotAsync,
            // Upgrades a specific resource field (by slot ID) to a target level.
            ["upgrade_resource_to_level"] = ExecuteUpgradeResourceToLevelAsync,
            // Upgrades a specific resource field (by slot ID) to its maximum possible level.
            ["upgrade_all_resources_to_level"] = ExecuteUpgradeAllResourcesToLevelAsync,
            // Upgrades a specific building (by slot ID) to a target level.
            ["upgrade_building_to_level"] = ExecuteUpgradeBuildingToLevelAsync,
            // Upgrades a specific building (by slot ID) to its maximum possible level.
            ["upgrade_building_to_max"] = ExecuteUpgradeBuildingToMaxAsync,
            // Constructs a new building in a specified slot using its GID.
            ["construct_building"] = ExecuteConstructBuildingAsync,
            // Loads the current village's building status and saves a JSON snapshot to disk.
            ["load_buildings_snapshot"] = ExecuteLoadBuildingsSnapshotAsync,
            // Demolishes a specified building to a target level.
            ["demolish_building_to_level"] = ExecuteDemolishBuildingToLevelAsync,
            // Manages hero actions: revives if dead, allocates points, and sends on adventures if HP allows.
            ["hero_manage"] = ExecuteHeroManageAsync,
            // Opens the hero attributes tab and sets the Hide hero / stay-with-troops radio.
            ["hero_set_hide_mode"] = ExecuteHeroSetHideModeAsync,
            // Walks through the Smithy and clicks every "Upgrade" button until none remain.
            ["upgrade_troops_at_smithy"] = ExecuteUpgradeTroopsAtSmithyAsync,
            // Builds troops from Barracks, Stable, or Workshop based on configured rules.
            ["build_troops"] = ExecuteBuildTroopsAsync,
            // Starts or tracks the Teutons brewery celebration.
            ["run_brewery_celebration"] = ExecuteRunBreweryCelebrationAsync,
            // Sends one of the selected farmlists that is ready right now.
            ["send_farmlists"] = ExecuteSendFarmlistsAsync,
            // Sends surplus resources from selected own villages to one target village.
            ["send_resources_between_villages"] = ExecuteSendResourcesBetweenVillagesAsync,
            // Sends selected troops from selected own villages to one target village as reinforcements.
            ["send_reinforcements_between_villages"] = ExecuteSendReinforcementsBetweenVillagesAsync,
            // Official: collects achieved Questmaster task rewards on the /tasks page (both tabs).
            ["collect_tasks"] = ExecuteCollectTasksAsync,
            // Official: collects claimable Daily Quests rewards from the topbar React dialog.
            ["collect_daily_quests"] = ExecuteCollectDailyQuestsAsync,
        };

    private readonly IAccountProvider _accountProvider;
    private readonly ProjectContext _projectContext;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly ICaptchaAutoSolver _captchaAutoSolver;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private BrowserSession? _sharedVisibleSession;
    private IPage? _sharedVisiblePage;
    private IPage? _travcoPage;
    private string? _sharedVisibleAccountName;
    private string? _sharedVisibleBaseUrl;
    // Session-scoped cache shared by every TravianClient created for the shared visible browser, so
    // feature signals (Plus/GoldClub/tribe) and the logged-in throttle survive across operations.
    private TravianSessionCache _sharedVisibleSessionCache = new();
    private int _browserClosedByUserSignal;

    public BotTaskRunner(IAccountProvider accountProvider, ProjectContext projectContext, ICaptchaAutoSolver captchaAutoSolver)
    {
        _accountProvider = accountProvider;
        _projectContext = projectContext;
        _captchaAutoSolver = captchaAutoSolver;
        _accountAnalysisStore = new AccountAnalysisStore(projectContext.RootPath);
    }

    public static IReadOnlyList<string> RegisteredTaskNames => TaskHandlers.Keys.ToList();

    public async Task<IReadOnlyList<MapOasisEntry>> ScanMapOasesAsync(
        BotOptions options,
        bool includeOccupied,
        IReadOnlyCollection<string> selectedTypes,
        Action<string> log,
        IProgress<MapOasisScanProgress>? progress,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MapOasisEntry>? result = null;
        try
        {
            await ExecuteWithClientAsync(
                options,
                log,
                accountName,
                interactive: true,
                cancellationToken,
                async client =>
                {
                    result = await client.ScanMapOasesAsync(
                        includeOccupied,
                        selectedTypes,
                        progress,
                        cancellationToken);
                });
        }
        catch (OperationCanceledException)
        {
            log("[map-oasis] scan canceled.");
            throw;
        }
        catch (Exception ex)
        {
            log($"[map-oasis] scan failed: {ex}");
            throw new InvalidOperationException($"Map oasis analysis failed: {ex.Message}", ex);
        }

        return result ?? [];
    }

    public async Task ExecuteOnceAsync(
        BotOptions options,
        Action<string> log,
        IEnumerable<string>? tasksOverride = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = tasksOverride?.ToList() ?? (options.LoopTasks is { Count: > 0 } configuredTasks ? configuredTasks : ["status"]);
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                var tickSw = System.Diagnostics.Stopwatch.StartNew();
                log($"[tick] starting — account='{client.AccountName}' server='{options.ServerName}' targetVillage='{options.TargetVillageName ?? "(default)"}'");
                log($"[tick] tasks ({tasks.Count}): {string.Join(", ", tasks)}");

                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                var context = new TaskExecutionContext(this, options, client, log, cancellationToken);
                var taskIndex = 0;
                foreach (var taskName in tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    taskIndex++;
                    if (!TaskCatalog.IsAllowed(taskName))
                    {
                        log($"[tick] task '{taskName}' is not allowed — skipping ({taskIndex}/{tasks.Count})");
                        continue;
                    }

                    if (!TaskHandlers.TryGetValue(taskName, out var handler))
                    {
                        log($"[tick] task '{taskName}' is allowed but not implemented — skipping ({taskIndex}/{tasks.Count})");
                        continue;
                    }

                    var taskSw = System.Diagnostics.Stopwatch.StartNew();
                    log($"[{taskName} STARTED] ({taskIndex}/{tasks.Count}) on '{client.AccountName}'");
                    try
                    {
                        await handler(context);
                        log($"[{taskName} COMPLETED] in {taskSw.Elapsed.TotalSeconds:F1}s ({taskIndex}/{tasks.Count})");
                    }
                    catch (OperationCanceledException)
                    {
                        log($"[{taskName} CANCELED] after {taskSw.Elapsed.TotalSeconds:F1}s ({taskIndex}/{tasks.Count})");
                        throw;
                    }
                    catch (TaskWaitException waitEx)
                    {
                        // Not a failure — this is the worker telling the queue "I can't make progress
                        // right now (resources/queue/cooldown), retry me in N seconds". The outer
                        // loop already logs a [LOOP n] DEFER line. Don't trip the alarm panel.
                        log($"[{taskName} DEFERRED] after {taskSw.Elapsed.TotalSeconds:F1}s — wait {waitEx.DelaySeconds}s: {waitEx.Message}");
                        throw;
                    }
                    catch (TaskBlockedPermanentlyException blockedEx)
                    {
                        // Permanent block (e.g. building at max, required building missing). Worth
                        // noting clearly but not the same as an unexpected crash.
                        log($"[{taskName} BLOCKED] after {taskSw.Elapsed.TotalSeconds:F1}s: {blockedEx.Message}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log($"[{taskName} FAILED] after {taskSw.Elapsed.TotalSeconds:F1}s: {ex.GetType().Name}: {ex.Message}");
                        throw;
                    }
                }
                log($"[tick] completed in {tickSw.Elapsed.TotalSeconds:F1}s ({tasks.Count} task(s)) on '{client.AccountName}'");
            });
    }

    public async Task<bool> IsLoggedInAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var isLoggedIn = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                isLoggedIn = await client.CheckLoggedInAsync(cancellationToken);
            });

        return isLoggedIn;
    }

    public async Task ExecuteLoginAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default,
        bool keepBrowserOpenAfterLogin = false)
    {
        _ = keepBrowserOpenAfterLogin;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                log($"Starting login for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                log("Login completed and browser session saved.");
                if (!options.Headless)
                {
                    log("Browser stays open (headless is disabled).");
                }
            });
    }

    public async Task<PostLoginSnapshot> ExecuteLoginAndLoadPostLoginSnapshotAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default,
        bool keepBrowserOpenAfterLogin = false)
    {
        _ = keepBrowserOpenAfterLogin;
        PostLoginSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                log($"Starting login for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                log("Login completed and browser session saved.");
                if (!options.Headless)
                {
                    log("Browser stays open (headless is disabled).");
                }

                snapshot = await LoadPostLoginSnapshotAsync(client, options, log, cancellationToken);
            });

        return snapshot ?? throw new InvalidOperationException("Could not load post-login snapshot.");
    }

    public async Task<PostLoginSnapshot> LoadPostLoginSnapshotAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        PostLoginSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                snapshot = await LoadPostLoginSnapshotAsync(client, options, log, cancellationToken);
            });

        return snapshot ?? throw new InvalidOperationException("Could not load post-login snapshot.");
    }

    private async Task<PostLoginSnapshot> LoadPostLoginSnapshotAsync(
        TravianClient client,
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        log($"Loading post-login data for server {options.ServerName}.");

        // When enabled, read the hero inventory FIRST — right after login and before the profile
        // navigation (ReadAccountSnapshotAsync reads villages from spieler.php/profile).
        HeroInventoryResources? heroInventory = null;
        if (options.PostLoginAnalyzeHeroInventory)
        {
            // Suppress the village/profile UI-sync so the inventory is read before the profile nav.
            heroInventory = await client.ReadHeroInventoryResourcesAsync(cancellationToken, suppressUiSync: true);
        }

        var accountSnapshot = await client.ReadAccountSnapshotAsync(
            forceRefreshVillages: true,
            preferCurrentPageVillages: false,
            restorePageAfterProfile: false,
            suppressEnsureUiSync: true,
            // We just read the hero inventory and will refresh villages from the profile next —
            // skip the redundant dorf1 hop in that case.
            skipOverviewNavigation: heroInventory is not null,
            cancellationToken);

        var buildingStatus = await client.ReadBuildingsStatusAsync(cancellationToken);
        var villageStatus = await client.ReadVillageStatusAsync(
            accountSnapshot.Villages,
            buildingStatus.Buildings,
            cancellationToken);

        if (options.PostLoginReadTroopTrainingQueue)
        {
            var troopQueues = await client.ReadTroopTrainingQueuesAsync(villageStatus.Buildings, cancellationToken);
            villageStatus = villageStatus with { TroopTrainingQueues = troopQueues };
        }

        var inboxStatus = new InboxStatus(villageStatus.UnreadMessages, villageStatus.UnreadReports);
        var adventureCount = await client.RefreshAdventureCountAsync(forceReload: false, cancellationToken);

        PersistStableAccountSignals(client, accountSnapshot.Tribe, log);

        return new PostLoginSnapshot(villageStatus, inboxStatus, adventureCount, heroInventory);
    }

    private void PersistStableAccountSignals(
        TravianClient client,
        string? fallbackTribe,
        Action<string> log)
    {
        _accountAnalysisStore.TryLoad(client.AccountName, out var existing, client.ServerUrl);

        var tribe = IsKnownTribe(client.KnownTribe)
            ? client.KnownTribe!
            : IsKnownTribe(fallbackTribe)
                ? fallbackTribe!
                : existing?.Tribe ?? "Unknown";
        var goldClubEnabled = client.KnownGoldClubEnabled == true || existing?.GoldClubEnabled == true;

        if (!IsKnownTribe(tribe) && !goldClubEnabled)
        {
            return;
        }

        var completed = new AccountAnalysisSnapshot(
            SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: client.AccountName,
            ServerUrl: client.ServerUrl,
            Tribe: IsKnownTribe(tribe) ? tribe : "Unknown",
            GoldClubEnabled: goldClubEnabled,
            BuildingCatalog: existing?.BuildingCatalog ?? (IsKnownTribe(tribe) ? BuildingCatalogService.GetCatalogForTribe(tribe) : []),
            AutoCelebrationEnabled: existing?.AutoCelebrationEnabled,
            AutomationLoopEnabledGroups: existing?.AutomationLoopEnabledGroups,
            AutomationLoopVisibleGroups: existing?.AutomationLoopVisibleGroups);

        _accountAnalysisStore.Save(completed);
        log($"[cache] stable account signals saved for '{completed.AccountName}' (tribe={completed.Tribe}, goldclub={completed.GoldClubEnabled}).");
        // Emit the real-time signal the desktop UI parses (GoldClubStatusRegex) so the Gold Club
        // indicator flips at login instead of waiting for the next stored-analysis read (~1 min later).
        log($"[goldclub] active={goldClubEnabled}");
    }

    private static bool IsKnownTribe(string? tribe)
        => !string.IsNullOrWhiteSpace(tribe)
           && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> ReadAndPersistGoldClubStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var account = _accountProvider.LoadAccount(accountName);
        _accountAnalysisStore.TryLoad(account.Name, out var existing, options.BaseUrl);
        var detectedGoldClubEnabled = false;
        var serverUrl = options.BaseUrl.TrimEnd('/');
        var tribe = existing?.Tribe ?? "Unknown";

        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                detectedGoldClubEnabled = await client.ReadGoldClubStatusAsync(cancellationToken);
                serverUrl = client.ServerUrl;
                if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = await client.ReadAccountSnapshotAsync(cancellationToken: cancellationToken);
                    tribe = snapshot.Tribe;
                }
            });

        var effectiveGoldClubEnabled = detectedGoldClubEnabled || (existing?.GoldClubEnabled ?? false);
        var completed = new AccountAnalysisSnapshot(
            SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: account.Name,
            ServerUrl: serverUrl,
            Tribe: string.IsNullOrWhiteSpace(tribe) ? "Unknown" : tribe,
            GoldClubEnabled: effectiveGoldClubEnabled,
            BuildingCatalog: existing?.BuildingCatalog ?? [],
            AutoCelebrationEnabled: existing?.AutoCelebrationEnabled,
            AutomationLoopEnabledGroups: existing?.AutomationLoopEnabledGroups,
            AutomationLoopVisibleGroups: existing?.AutomationLoopVisibleGroups);

        _accountAnalysisStore.Save(completed);
        log($"Gold Club status saved for '{completed.AccountName}': {(completed.GoldClubEnabled ? "Yes" : "No")}.");
        return completed.GoldClubEnabled;
    }

    public async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FarmListOverview> overview = [];
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                overview = await client.ReadFarmListsOverviewAsync(cancellationToken);
            });

        return overview;
    }

    public async Task<int?> SendFarmListNowAsync(
        BotOptions options,
        string farmListName,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        int? remainingSeconds = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                remainingSeconds = await client.SendFarmListNowAsync(farmListName, cancellationToken);
            });

        return remainingSeconds;
    }

    public async Task<FarmAddResult> AddSingleFarmFromNatarsAsync(
        BotOptions options,
        string farmListName,
        string troopType,
        int troopCount,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        FarmAddResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.AddSingleFarmFromNatarsAsync(farmListName, troopType, troopCount, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not add farm from Natars profile.");
    }

    public async Task<FarmAddBatchResult> AddFarmsFromNatarsAsync(
        BotOptions options,
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        Action<string> log,
        string? accountName = null,
        IProgress<int>? addedProgress = null,
        CancellationToken cancellationToken = default)
    {
        FarmAddBatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.AddFarmsFromNatarsAsync(farmListName, troopType, troopCount, requestedCount, addedProgress, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not add farms from Natars profile.");
    }

    public async Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(
        BotOptions options,
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops,
        Action<string> log,
        string? accountName = null,
        IProgress<FarmAddProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FarmAddBatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.AddFarmsFromCoordinatesAsync(
                    farmListName,
                    troopType,
                    troopCount,
                    requestedCount,
                    coordinates,
                    useDefaultTroops,
                    progress,
                    cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not add farms from Travco list.");
    }

    public async Task<FarmListCreateBatchResult> CreateFarmListsAsync(
        BotOptions options,
        FarmListCreateRequest request,
        Action<string> log,
        string? accountName = null,
        IProgress<FarmListCreateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FarmListCreateBatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.CreateFarmListsAsync(request, progress, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not create farm lists.");
    }

    public async Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh = false,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        // Defensive guard: Natar villages only exist on the SS-Travi private server.
        // Skip the operation entirely on official servers, even if invoked programmatically
        // (e.g. from the continuous loop), so the bot never navigates to the Natar profile there.
        if (!options.IsPrivateServer)
        {
            log("Natar farm analysis skipped: only available on the SS-Travi private server.");
            return 0;
        }

        var count = 0;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                count = await client.EnsureNatarFarmCacheAndReturnToFarmListAsync(forceRefresh, cancellationToken);
            });

        return count;
    }

    public async Task<ManualFarmRunResult> StartManualFarmingFromNatarsAsync(
        BotOptions options,
        string troopType,
        long troopCount,
        int troopVariancePercent,
        bool raidAttack,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        ManualFarmRunResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.StartManualFarmingFromNatarsAsync(troopType, troopCount, troopVariancePercent, raidAttack, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not start manual farming from Natars profile.");
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        return await ReadAvailableTroopsForCatapultWavesAsync(
            options,
            log,
            forceRefresh: false,
            accountName,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, long> result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.ReadAvailableTroopsForCatapultWavesAsync(forceRefresh, cancellationToken);
            });

        return result;
    }

    public async Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        CatapultWaveSetupInfo? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.ReadCatapultWaveSetupInfoAsync(forceRefresh, cancellationToken);
            });

        return result ?? new CatapultWaveSetupInfo(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), null);
    }

    public async Task<CatapultWaveRunResult> StartCatapultWavesAsync(
        BotOptions options,
        CatapultWaveRequest request,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        CatapultWaveRunResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.StartCatapultWavesAsync(request, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not start catapult waves.");
    }

    public async Task<VillageStatus> ReadVillageStatusAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading village status for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                status = await client.ReadVillageStatusAsync(cancellationToken);
            });

        return status ?? throw new InvalidOperationException("Could not read village status.");
    }

    public async Task<VillageStatus> ReadVillageResourceStatusAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        bool currentPageOnly = false,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading village resource status for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                if (!currentPageOnly)
                {
                    await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                }

                status = await client.ReadVillageResourceStatusAsync(
                    cancellationToken,
                    allowNavigationToResourcePage: !currentPageOnly);
                var forecastCount = status?.ResourceStorageForecasts?.Count ?? 0;
                var warehouse = FormatResourceStatusNumber(status?.WarehouseCapacity);
                var granary = FormatResourceStatusNumber(status?.GranaryCapacity);
                log($"Resource status: village='{status?.ActiveVillage ?? "-"}', fields={status?.ResourceFields.Count ?? 0}, forecasts={forecastCount}, storage={warehouse}/{granary}.");
            });

        return status ?? throw new InvalidOperationException("Could not read village resource status.");
    }

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageResourceStatusesAsync(
        BotOptions options,
        Action<string> log,
        string? returnVillageName = null,
        string? returnVillageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VillageStatus> statuses = [];
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Scanning resource status for all villages on server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                try
                {
                    statuses = await client.ReadAllVillageResourceStatusesAsync(cancellationToken);
                    log($"All-village resource scan read {statuses.Count} village(s).");
                }
                finally
                {
                    var targetName = string.IsNullOrWhiteSpace(returnVillageName) ? options.TargetVillageName : returnVillageName;
                    var targetUrl = string.IsNullOrWhiteSpace(returnVillageUrl) ? options.TargetVillageUrl : returnVillageUrl;
                    if (!string.IsNullOrWhiteSpace(targetName) || !string.IsNullOrWhiteSpace(targetUrl))
                    {
                        try
                        {
                            await client.SwitchToVillageAsync(targetName ?? string.Empty, targetUrl, CancellationToken.None, skipFeatureRefresh: true);
                            var label = !string.IsNullOrWhiteSpace(targetName) ? targetName : targetUrl;
                            log($"Returned to selected village: {label}");
                        }
                        catch (Exception ex)
                        {
                            log($"Could not return to selected village after scan: {ex.Message}");
                        }
                    }
                }
            });

        return statuses;
    }

    public async Task<VillageStatus> ReadCurrentPageResourceStatusQuickAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                status = await client.ReadVillageResourceStatusAsync(
                    cancellationToken,
                    allowNavigationToResourcePage: false);
            });

        return status ?? throw new InvalidOperationException("Could not read current-page resource status.");
    }

    public async Task<VillageStatus> ReadCurrentPageStorageStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                status = await client.ReadCurrentPageStorageStatusAsync(cancellationToken);
            });

        return status ?? throw new InvalidOperationException("Could not read current-page storage status.");
    }

    public async Task<PageHtmlCapture> ReadCurrentPageHtmlAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        PageHtmlCapture? capture = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                capture = await client.ReadCurrentPageHtmlAsync(cancellationToken);
            });

        return capture ?? throw new InvalidOperationException("Could not read current page HTML.");
    }

    public async Task<PageHtmlCapture> NavigateToPageAndReadHtmlAsync(
        BotOptions options,
        string pagePath,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        PageHtmlCapture? capture = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                capture = await client.NavigateToPageAndReadHtmlAsync(pagePath, cancellationToken);
            });

        return capture ?? throw new InvalidOperationException($"Could not save page HTML for {pagePath}.");
    }

    public async Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionPerHourAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, double?>? productionByHour = null;
        log($"Production-only resource read for server {options.ServerName}.");
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                productionByHour = await client.ReadCurrentPageResourceProductionPerHourAsync(cancellationToken);
            });

        if (productionByHour is not null)
        {
            var parts = new List<string>(4);
            foreach (var key in new[] { "wood", "clay", "iron", "crop" })
            {
                productionByHour.TryGetValue(key, out var value);
                var formatted = value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                parts.Add($"{key}={formatted}/h");
            }

            log($"Production-only resource read result: {string.Join(", ", parts)}");
        }

        return productionByHour ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatResourceStatusNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
    }

    public async Task<VillageStatus> ReadBuildingsStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading buildings status for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                status = await client.ReadBuildingsStatusAsync(cancellationToken);
            });

        return status ?? throw new InvalidOperationException("Could not read buildings status.");
    }

    public async Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadTroopTrainingQueuesAsync(
        BotOptions options,
        Action<string> log,
        IReadOnlyList<Building>? knownBuildings = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TroopTrainingQueueStatus> statuses = [];
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                statuses = await client.ReadTroopTrainingQueuesAsync(knownBuildings, cancellationToken);
            });

        return statuses;
    }

    public async Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(
        BotOptions options,
        Action<string> log,
        IReadOnlyList<Building>? knownBuildings = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        BreweryCelebrationStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                status = await client.ReadBreweryCelebrationStatusAsync(knownBuildings, cancellationToken);
            });

        return status ?? new BreweryCelebrationStatus(false, null, false, null, false, null, "N/A", "Status unavailable.");
    }

    public async Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(
        BotOptions options,
        Action<string> log,
        IReadOnlyList<Building>? knownBuildings = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        SmithyUpgradeStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                status = await client.ReadSmithyUpgradeStatusAsync(knownBuildings, cancellationToken);
            });

        return status ?? new SmithyUpgradeStatus(false, null, 0, null, [], "N/A", "Status unavailable.");
    }

    public async Task<string> RunNpcTradeForBuildingTestAsync(
        BotOptions options,
        Action<string> log,
        TbotUltra.Core.Travian.TroopTrainingBuildingType buildingType,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "NPC trade test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                result = await client.RunNpcTradeForBuildingTestAsync(buildingType, cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunNpcTradeForCurrentBuildingPageTestAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "NPC trade building test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                result = await client.RunNpcTradeForCurrentBuildingPageTestAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> ReadSmithyQueueFromCurrentPageTestAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Smithy queue test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                result = await client.ReadSmithyQueueFromCurrentPageTestAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunReinforcementsTestAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Reinforcements test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.TestSendReinforcementsBetweenOwnVillagesAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunBreweryCelebrationAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Brewery celebration: status unavailable.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                result = await client.RunBreweryCelebrationAsync(cancellationToken);
            });

        return result;
    }

    public async Task<InboxStatus> ReadInboxStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        InboxStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading inbox status for server {options.ServerName}.");
                status = await client.ReadInboxStatusAsync(cancellationToken);
            });

        return status ?? new InboxStatus();
    }

    public async Task NavigateToVillageResourceFieldsAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                await client.NavigateToResourceFieldsAsync(cancellationToken);
            });
    }

    // Reloads the page the browser is currently on (no navigation), used by the continuous loop's
    // idle keep-alive to stop the Travian page from going stale and showing wrong values.
    public async Task RefreshCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await client.RefreshCurrentPageAsync(cancellationToken);
            });
    }

    public async Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        HeroAdventureDispatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.SendHeroOnAdventureAsync(cancellationToken);
                log(result.Message);
            });

        return result ?? throw new InvalidOperationException("Could not dispatch hero on adventure.");
    }

    public async Task<bool> CheckAndReviveDeadHeroAsync(
        BotOptions options,
        bool autoRevive,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var revived = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                revived = await client.CheckAndReviveDeadHeroOnCurrentPageAsync(autoRevive, cancellationToken);
            });

        return revived;
    }

    public async Task<int?> RefreshAdventureCountAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        int? count = null;
        var found = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                count = await client.RefreshAdventureCountAsync(cancellationToken: cancellationToken);
                found = count is not null;
            });

        if (!found)
        {
            log("Adventures not found on current page.");
        }
        else
        {
            log($"Adventures available: {count}.");
        }

        return count;
    }

    // Cheap current-page probe (no navigation) used by the periodic refresh to decide whether
    // to queue collect_tasks. Returns false on any failure so it never disrupts the refresh.
    public async Task<bool> HasClaimableTasksOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var claimable = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                claimable = await client.HasClaimableTasksOnCurrentPageAsync(cancellationToken);
            });

        return claimable;
    }

    // Cheap current-page probe (no navigation) used by the periodic refresh to decide whether
    // to queue collect_daily_quests. Returns false on any failure so it never disrupts the refresh.
    public async Task<bool> HasClaimableDailyQuestsOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var claimable = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                claimable = await client.HasClaimableDailyQuestsOnCurrentPageAsync(cancellationToken);
            });

        return claimable;
    }

    public async Task<HeroAttributeSnapshot> ReadHeroAttributesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        HeroAttributeSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                snapshot = await client.ReadHeroAttributeSnapshotAsync(cancellationToken);
                log(
                    $"Hero attributes: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}, adventures={(snapshot.AdventureCount?.ToString() ?? "?")}.");
            });

        return snapshot ?? throw new InvalidOperationException("Could not read hero attributes.");
    }

    public async Task<HeroInventoryResources> ReadHeroInventoryResourcesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        HeroInventoryResources? resources = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                resources = await client.ReadHeroInventoryResourcesAsync(cancellationToken);
                log($"Hero inventory: wood={resources.Wood}, clay={resources.Clay}, iron={resources.Iron}, crop={resources.Crop}.");
            });

        return resources ?? throw new InvalidOperationException("Could not read hero inventory resources.");
    }

    public async Task ExecuteLogoutAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Starting logout for server {options.ServerName}.");
                await client.LogoutAsync(cancellationToken);
                log("Logout completed.");
                // Drop all session-scoped cache (villages, population, plus/gold, logged-in state)
                // so a subsequent login on this shared browser starts from a clean slate and never
                // reuses the logged-out account's data.
                _sharedVisibleSessionCache = new TravianSessionCache();
            });
    }

    public async Task MarkMessagesAsReadAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                var changed = await client.MarkMessagesAsReadAsync(cancellationToken);
                log(changed ? "[inbox] messages marked as read." : "[inbox] no unread messages to mark as read.");
            });
    }

    public async Task MarkReportsAsReadAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                var changed = await client.MarkReportsAsReadAsync(cancellationToken);
                log(changed ? "[inbox] reports marked as read." : "[inbox] no unread reports to mark as read.");
            });
    }

    public async Task ShutdownAsync(Action<string>? log = null)
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_travcoPage is not null)
            {
                try
                {
                    await ClearTravcoSiteDataAsync(_travcoPage, log);
                    if (!_travcoPage.IsClosed)
                    {
                        await _travcoPage.CloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Error while closing Travco tab: {ex.Message}");
                }
                finally
                {
                    _travcoPage = null;
                }
            }

            if (_sharedVisibleSession is null)
            {
                return;
            }

            try
            {
                await _sharedVisibleSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Error while closing shared browser: {ex.Message}");
            }
            finally
            {
                _sharedVisibleSession = null;
                _sharedVisiblePage = null;
                _sharedVisibleAccountName = null;
                _sharedVisibleBaseUrl = null;
                _sharedVisibleSessionCache = new TravianSessionCache();
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task OpenTravcoAndSearchAsync(
        BotOptions options,
        TravcoSearchRequest request,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (options.IsPrivateServer)
        {
            throw new InvalidOperationException("Travco inactive search supports official Travian servers only.");
        }

        if (options.Headless)
        {
            throw new InvalidOperationException("Travco inactive search requires the visible browser session.");
        }

        var account = _accountProvider.LoadAccount();
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var lease = await AcquireClientLeaseAsync(options, account, log, interactive: true, cancellationToken);
            try
            {
                if (_travcoPage is null || _travcoPage.IsClosed)
                {
                    _travcoPage = await _sharedVisiblePage!.Context.NewPageAsync();
                    // Travco can be slow to render; raise the default 15s context timeout for this tab
                    // so individual navigations don't trip the timeout on a sluggish load.
                    _travcoPage.SetDefaultTimeout(30000);
                    log("[travco] opened browser tab.");
                }

                await TravcoInactiveSearch.RunSearchAsync(
                    _travcoPage,
                    new Uri(options.BaseUrl).Host,
                    request.X,
                    request.Y,
                    request.DaysInactive,
                    request.OrderBy,
                    resultsPerPage: 100,
                    log,
                    cancellationToken);
                await _travcoPage.BringToFrontAsync();
            }
            finally
            {
                await FinalizeLeaseAsync(lease, log);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<TravcoScrapeResult> ScrapeTravcoPageAsync(
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_travcoPage is null || _travcoPage.IsClosed)
            {
                throw new InvalidOperationException("Open and run a Travco inactive search first.");
            }

            return await TravcoInactiveSearch.ScrapePageAsync(_travcoPage, log, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<TravcoScrapeResult> ScrapeAllTravcoPagesAsync(
        Action<string> log,
        IProgress<(int CurrentPage, int TotalPages)> progress,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_travcoPage is null || _travcoPage.IsClosed)
            {
                throw new InvalidOperationException("Open and run a Travco inactive search first.");
            }

            return await TravcoInactiveSearch.ScrapeAllPagesAsync(
                _travcoPage,
                log,
                progress,
                cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task CloseTravcoTabAsync(Action<string>? log = null)
    {
        if (_travcoPage is null)
        {
            log?.Invoke("[travco] no browser tab was open.");
            return;
        }

        await _sessionGate.WaitAsync();
        try
        {
            if (_travcoPage is null)
            {
                return;
            }

            try
            {
                await ClearTravcoSiteDataAsync(_travcoPage, log);
                if (!_travcoPage.IsClosed)
                {
                    await _travcoPage.CloseAsync();
                }

                log?.Invoke("[travco] browser tab closed.");
            }
            finally
            {
                _travcoPage = null;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // Clears travcotools.com site data (localStorage/cookies/etc.) from the shared browser context
    // before the Travco tab closes. Otherwise the origin lingers as a tracked origin and Playwright's
    // periodic StorageStateAsync (saved after every shared-session operation) reopens a short-lived
    // travcotools tab to read its storage — the "blinking" extra tab the user sees every ~16s.
    // Best-effort: never block tab close on a CDP failure.
    private static async Task ClearTravcoSiteDataAsync(IPage page, Action<string>? log)
    {
        if (page.IsClosed)
        {
            return;
        }

        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            try
            {
                await cdp.SendAsync("Storage.clearDataForOrigin", new Dictionary<string, object>
                {
                    ["origin"] = TravcoInactiveSearch.SiteOrigin,
                    ["storageTypes"] = "all",
                });
            }
            finally
            {
                await cdp.DetachAsync();
            }

            log?.Invoke("[travco] cleared travcotools site data from the browser context.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[travco] could not clear travcotools site data: {ex.Message}");
        }
    }

    private async Task ExecuteWithClientAsync(
        BotOptions options,
        Action<string> log,
        string? accountName,
        bool interactive,
        CancellationToken cancellationToken,
        Func<TravianClient, Task> action)
    {
        var account = _accountProvider.LoadAccount(accountName);
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var lease = await AcquireClientLeaseAsync(options, account, log, interactive, cancellationToken);
            try
            {
                await action(lease.Client);
            }
            finally
            {
                await FinalizeLeaseAsync(lease, log);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<ClientLease> AcquireClientLeaseAsync(
        BotOptions options,
        AccountOptions account,
        Action<string> log,
        bool interactive,
        CancellationToken cancellationToken)
    {
        if (options.Headless)
        {
            var session = new BrowserSession(options, account, _projectContext.RootPath, log: log);
            var page = await session.OpenPageAsync(cancellationToken);
            var sessionCache = CreateSeededSessionCache(account, options, log);
            var client = CreateClient(page, options, account, interactive, log, sessionCache);
            return new ClientLease(session, client, false);
        }

        var desiredBaseUrl = options.BaseUrl.TrimEnd('/');
        var replaceReasons = new List<string>();
        if (_sharedVisibleSession is null)
        {
            replaceReasons.Add("session=null");
        }

        if (_sharedVisiblePage is null)
        {
            replaceReasons.Add("page=null");
        }
        else if (_sharedVisiblePage.IsClosed)
        {
            replaceReasons.Add("page=closed");
        }

        if (!string.Equals(_sharedVisibleAccountName, account.Name, StringComparison.OrdinalIgnoreCase))
        {
            replaceReasons.Add($"account='{_sharedVisibleAccountName ?? "-"}'->'{account.Name}'");
        }

        if (!string.Equals(_sharedVisibleBaseUrl, desiredBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            replaceReasons.Add($"baseUrl='{_sharedVisibleBaseUrl ?? "-"}'->'{desiredBaseUrl}'");
        }

        var mustReplaceSession =
            _sharedVisibleSession is null ||
            _sharedVisiblePage is null ||
            _sharedVisiblePage.IsClosed ||
            !string.Equals(_sharedVisibleAccountName, account.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_sharedVisibleBaseUrl, desiredBaseUrl, StringComparison.OrdinalIgnoreCase);

        if (mustReplaceSession)
        {
            log($"[browser-session] replacing shared browser session. pages={TryGetSharedPageCount()} reason='{string.Join(", ", replaceReasons)}' currentUrl='{_sharedVisiblePage?.Url ?? "-"}'");
        }

        if (_sharedVisiblePage is not null && _sharedVisiblePage.IsClosed)
        {
            Interlocked.Exchange(ref _browserClosedByUserSignal, 1);
            log("Shared browser window was closed. A new browser session will be created.");
        }

        if (mustReplaceSession)
        {
            _travcoPage = null;
            if (_sharedVisibleSession is not null)
            {
                try
                {
                    await _sharedVisibleSession.DisposeAsync();
                }
                catch (Exception ex)
                {
                    log($"Shared browser cleanup failed: {ex.Message}");
                }
            }

            var session = new BrowserSession(options, account, _projectContext.RootPath, headlessOverride: false, log: log);
            var page = await session.OpenPageAsync(cancellationToken);
            _sharedVisibleSession = session;
            _sharedVisiblePage = page;
            _sharedVisibleAccountName = account.Name;
            _sharedVisibleBaseUrl = desiredBaseUrl;
            // Fresh browser/account => start a clean session cache so no stale signals carry over.
            _sharedVisibleSessionCache = CreateSeededSessionCache(account, options, log);
            log("Opened shared browser window.");
        }
        else
        {
            SeedStableAccountSignals(_sharedVisibleSessionCache, account, options, log);
        }

        var sharedClient = CreateClient(_sharedVisiblePage!, options, account, interactive, log, _sharedVisibleSessionCache);
        return new ClientLease(_sharedVisibleSession!, sharedClient, true);
    }

    private int TryGetSharedPageCount()
    {
        try
        {
            return _sharedVisiblePage?.Context.Pages.Count ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    public bool ConsumeBrowserClosedByUserSignal()
    {
        return Interlocked.Exchange(ref _browserClosedByUserSignal, 0) == 1;
    }

    private TravianClient CreateClient(
        IPage page,
        BotOptions options,
        AccountOptions account,
        bool interactive,
        Action<string> log,
        TravianSessionCache? sessionCache = null)
    {
        return new TravianClient(
            page,
            options,
            account,
            interactive: interactive,
            browserVisible: !options.Headless,
            projectRoot: _projectContext.RootPath,
            captchaAutoSolver: options.IsPrivateServer ? _captchaAutoSolver : null,
            statusCallback: log,
            sessionCache: sessionCache);
    }

    private TravianSessionCache CreateSeededSessionCache(
        AccountOptions account,
        BotOptions options,
        Action<string> log)
    {
        var sessionCache = new TravianSessionCache();
        SeedStableAccountSignals(sessionCache, account, options, log);
        return sessionCache;
    }

    private void SeedStableAccountSignals(
        TravianSessionCache sessionCache,
        AccountOptions account,
        BotOptions options,
        Action<string> log)
    {
        if (!_accountAnalysisStore.TryLoad(account.Name, out var analysis, options.BaseUrl))
        {
            return;
        }

        if (IsKnownTribe(analysis?.Tribe) && !IsKnownTribe(sessionCache.SessionTribe))
        {
            sessionCache.SessionTribe = analysis!.Tribe;
            sessionCache.CachedTribePlusAt = DateTimeOffset.UtcNow;
            log($"[cache] tribe='{analysis.Tribe}' loaded for '{account.Name}'.");
        }

        if (analysis?.GoldClubEnabled == true && sessionCache.CachedGoldClubEnabled != true)
        {
            sessionCache.CachedGoldClubEnabled = true;
            sessionCache.CachedTribePlusAt = DateTimeOffset.UtcNow;
            log($"[cache] goldclub=True loaded for '{account.Name}'.");
        }
    }

    private async Task FinalizeLeaseAsync(ClientLease lease, Action<string> log)
    {
        try
        {
            await lease.Session.SaveStateAsync();
        }
        catch (Exception ex)
        {
            log($"Could not save browser state: {ex.Message}");
        }

        if (!lease.KeepOpen)
        {
            await lease.Session.DisposeAsync();
        }
    }

    private static async Task ExecuteStatusAsync(TaskExecutionContext context)
    {
        var status = await context.Client.ReadVillageStatusAsync(context.CancellationToken);
        context.Log($"Village status read. ActiveVillage={status.ActiveVillage}, Villages={status.Villages.Count}, Resources={status.Resources.Count}, ResourceFields={status.ResourceFields.Count}, Buildings={status.Buildings.Count}, Queue={status.BuildQueue.Count}");
    }

    private static async Task ExecuteScanAllVillagesAsync(TaskExecutionContext context)
    {
        var statuses = await context.Client.ReadAllVillageStatusesAsync(context.CancellationToken);
        context.Log($"[scan] all villages scanned — {statuses.Count} status(es)");
    }

    private static async Task ExecuteAccountSnapshotAsync(TaskExecutionContext context)
    {
        var snapshot = await context.Client.ReadAccountSnapshotAsync(cancellationToken: context.CancellationToken);
        context.Log($"Account snapshot read. Tribe={snapshot.Tribe}, ActiveVillage={snapshot.ActiveVillage}, VillageCount={snapshot.VillageCount}, ServerTimeUtc={snapshot.ServerTimeUtc}");
    }

    private static async Task ExecuteUpgradeResourceToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.ResourceUpgradeSlotId is null || context.Options.ResourceUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_resource_to_level' requires config values resource_upgrade_slot_id and resource_upgrade_target_level.");
            return;
        }

        var result = await context.Client.UpgradeResourceToLevelAsync(
            context.Options.ResourceUpgradeSlotId.Value,
            context.Options.ResourceUpgradeTargetLevel.Value,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("upgrade_resource_to_level", result);
    }

    private static async Task ExecuteUpgradeAllResourcesToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.ResourceUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_all_resources_to_level' requires config value resource_upgrade_target_level.");
            return;
        }

        var result = await context.Client.UpgradeAllResourcesToLevelAsync(
            context.Options.ResourceUpgradeTargetLevel.Value,
            context.Options.ResourceBuildStrategy,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("upgrade_all_resources_to_level", result);
    }

    private static async Task ExecuteUpgradeBuildingToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingUpgradeSlotId is null || context.Options.BuildingUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_building_to_level' requires config values building_upgrade_slot_id and building_upgrade_target_level.");
            return;
        }

        var result = await context.Client.UpgradeBuildingToLevelAsync(
            context.Options.BuildingUpgradeSlotId.Value,
            context.Options.BuildingUpgradeTargetLevel.Value,
            context.CancellationToken);
        context.Log(result);
        // Desktop's HandleQueueItemSucceededAsync triggers RefreshConstructionStatusAsync
        // (fresh dorf1+dorf2 read) immediately after this task returns. A worker-side snapshot
        // read here would be discarded by that fresh read — skip it.
        ThrowIfTaskBlocked("upgrade_building_to_level", result);
    }

    private static async Task ExecuteUpgradeBuildingToMaxAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingUpgradeSlotId is null)
        {
            context.Log("Task 'upgrade_building_to_max' requires config value building_upgrade_slot_id.");
            return;
        }

        var result = await context.Client.UpgradeBuildingToMaxAsync(
            context.Options.BuildingUpgradeSlotId.Value,
            context.Options.BuildingUpgradeMaxAttempts,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("upgrade_building_to_max", result);
    }

    private static async Task ExecuteConstructBuildingAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingConstructSlotId is null || context.Options.BuildingConstructGid is null)
        {
            context.Log("Task 'construct_building' requires config values building_construct_slot_id and building_construct_gid.");
            return;
        }

        var buildingName = string.IsNullOrWhiteSpace(context.Options.BuildingConstructName)
            ? $"gid {context.Options.BuildingConstructGid.Value}"
            : context.Options.BuildingConstructName;

        var result = await context.Client.ConstructBuildingAsync(
            context.Options.BuildingConstructSlotId.Value,
            context.Options.BuildingConstructGid.Value,
            buildingName,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("construct_building", result);
    }

    private static async Task ExecuteUpgradeTroopsAtSmithyAsync(TaskExecutionContext context)
    {
        // No selection => no-op (the user hasn't picked any troops in 'Upgrade options'). Old queued tasks
        // carry no payload and therefore safely do nothing instead of blindly upgrading every troop.
        var targets = SmithyUpgradePayload.Parse(context.Options.SmithyUpgradeTargets);
        if (targets.Count == 0)
        {
            context.Log("Smithy: no troops selected for upgrade — configure them via 'Upgrade options'. Nothing to do.");
            return;
        }

        var result = await context.Client.UpgradeSelectedTroopsAtSmithyAsync(targets, context.CancellationToken);
        context.Log(result);
        await RefreshBuildingsSnapshotAfterTaskAsync(context);
        ThrowIfTroopsGroupBlocked(result);
        ThrowIfTaskBlocked("upgrade_troops_at_smithy", result);
    }

    private static async Task ExecuteBuildTroopsAsync(TaskExecutionContext context)
    {
        context.Log("[troops] build_troops starting");
        var result = await context.Client.BuildTroopsAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("build_troops", result);
    }

    private static async Task ExecuteRunBreweryCelebrationAsync(TaskExecutionContext context)
    {
        context.Log("[brewery] run_brewery_celebration starting");
        var result = await context.Client.RunBreweryCelebrationAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("run_brewery_celebration", result);
    }

    private static async Task ExecuteLoadBuildingsSnapshotAsync(TaskExecutionContext context)
    {
        var status = await context.Client.ReadBuildingsStatusAsync(context.CancellationToken);
        await WriteBuildingsSnapshotAsync(context, status);
        context.Log($"Loaded {status.Buildings.Count} building slots.");
    }

    private static async Task WriteBuildingsSnapshotAsync(TaskExecutionContext context, TbotUltra.Worker.Domain.VillageStatus status)
    {
        var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
        var safeAccount = string.IsNullOrWhiteSpace(activeAccount) ? "main" : activeAccount.Trim().ToLowerInvariant();
        var outputDir = Path.Combine(context.Runner._projectContext.RootPath, "temp_build_out", "buildings-snapshots");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{safeAccount}.json");

        var payload = new
        {
            account = activeAccount,
            activeVillage = status.ActiveVillage,
            tribe = status.Tribe,
            isCapital = status.IsCapital,
            buildings = status.Buildings.Select(building => new
            {
                slotId = building.SlotId,
                name = building.Name,
                level = building.Level,
                url = building.Url,
                gid = building.Gid,
            }).ToList(),
            resourceFields = status.ResourceFields.Select(field => new
            {
                slotId = field.SlotId,
                fieldType = field.FieldType,
                name = field.Name,
                level = field.Level,
                url = field.Url,
            }).ToList(),
        };

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
    }

    private static async Task WriteFarmListsSnapshotAsync(TaskExecutionContext context, IReadOnlyList<FarmListOverview> overview)
    {
        try
        {
            var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
            var outputPath = AccountStoragePaths.FarmListsSnapshotPath(context.Runner._projectContext.RootPath, activeAccount);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var payload = new
            {
                account = activeAccount,
                capturedAtUtc = DateTimeOffset.UtcNow,
                lists = overview
                    .Where(item => item is not null)
                    .Select(item => new
                    {
                        name = item.Name,
                        activeFarmCount = item.ActiveFarmCount,
                        totalFarmCount = item.TotalFarmCount,
                        remainingSeconds = item.RemainingSeconds,
                        listId = item.ListId,
                        capacity = item.Capacity,
                        farmCoordinates = item.FarmCoordinates,
                    })
                    .ToList(),
            };

            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
        }
        catch (Exception ex)
        {
            context.Log($"Could not write farm list snapshot: {ex.Message}");
        }
    }

    private static async Task RefreshBuildingsSnapshotAfterTaskAsync(TaskExecutionContext context)
    {
        try
        {
            var status = await context.Client.ReadBuildingsStatusAsync(context.CancellationToken);
            await WriteBuildingsSnapshotAsync(context, status);
            context.Log($"Buildings snapshot refreshed ({status.Buildings.Count} slots).");
        }
        catch (Exception ex)
        {
            context.Log($"Could not refresh buildings snapshot: {ex.Message}");
        }
    }

    private static async Task ExecuteDemolishBuildingToLevelAsync(TaskExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Options.TargetBuildingSlotOrName) || context.Options.TargetLevel is null)
        {
            context.Log("Task 'demolish_building_to_level' requires config values target_building_slot_or_name and target_level.");
            return;
        }

        var result = await context.Client.DemolishBuildingToLevelAsync(
            context.Options.TargetBuildingSlotOrName,
            context.Options.TargetLevel.Value,
            context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteHeroSetHideModeAsync(TaskExecutionContext context)
    {
        if (!context.Options.HeroHideModeEnabled)
        {
            context.Log("Hero hide mode control is disabled. No Travian hide/fight change was made.");
            return;
        }

        var result = await context.Client.SetHeroHideModeOnlyAsync(context.Options.HeroHideMode, context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteSendFarmlistsAsync(TaskExecutionContext context)
    {
        var selectedNames = (context.Options.ContinuousFarmListNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedIds = (context.Options.ContinuousFarmListIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedNames.Count <= 0 && selectedIds.Count <= 0)
        {
            throw new InvalidOperationException("No farm lists selected for continuous farming.");
        }

        // Match by the stable list id (lid) first so a renamed village/list still resolves; fall
        // back to name for selections saved before lids existed or lists without a resolvable lid.
        bool IsSelected(FarmListOverview item) =>
            (item.ListId is not null && selectedIds.Contains(item.ListId))
            || selectedNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase);

        var overview = await context.Client.ReadFarmListsOverviewAsync(context.CancellationToken);
        var matchingLists = overview
            .Where(item => item is not null && IsSelected(item))
            .OrderBy(item => item.RemainingSeconds is > 0 ? item.RemainingSeconds.Value : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matchingLists.Count <= 0)
        {
            // The selection is stored by farm-list name. If the user renamed a village/list on
            // Travian, the saved names no longer match the freshly read page. Don't raise a hard
            // alarm — defer quietly so the desktop UI can re-analyze and surface the current names
            // for re-selection. Embedding queue_wait_seconds routes this through the defer path.
            var retryWaitSeconds = Math.Clamp(context.Options.ContinuousFarmDispatchDelayMinutes, 1, 5) * 60;
            context.Log(overview.Count > 0
                ? $"Continuous farming: none of the selected farm lists ({string.Join(", ", selectedNames)}) were found on the farm page. They may have been renamed — re-analyze and re-select. Retrying in {retryWaitSeconds}s."
                : $"Continuous farming: no farm lists were found on the farm page. Retrying in {retryWaitSeconds}s.");
            throw new InvalidOperationException($"Selected farm lists were not found on the farm page. queue_wait_seconds={Math.Max(1, retryWaitSeconds)}");
        }

        var readyLists = matchingLists
            .Where(item => item.RemainingSeconds is null or <= 0)
            .ToList();
        if (readyLists.Count > 0)
        {
            context.Log($"Continuous farming ready lists: {string.Join(", ", readyLists.Select(item => item.Name))}");
        }

        var ready = readyLists.FirstOrDefault();
        if (ready is null)
        {
            var waitSeconds = matchingLists
                .Where(item => item.RemainingSeconds is > 0)
                .Min(item => item.RemainingSeconds) ?? 60;
            context.Log($"Continuous farming: no selected list is ready. Shortest remaining time={waitSeconds}s.");
            throw new InvalidOperationException($"No selected farm list is ready. queue_wait_seconds={Math.Max(1, waitSeconds)}");
        }

        var remainingSeconds = await context.Client.SendFarmListNowAsync(ready.Name, context.CancellationToken);
        var dispatchDelaySeconds = Math.Clamp(context.Options.ContinuousFarmDispatchDelayMinutes, 1, 5) * 60;
        context.Log($"Continuous farming sending list '{ready.Name}'. Delay between sends={dispatchDelaySeconds}s.");

        var refreshedOverview = await context.Client.ReadFarmListsOverviewAsync(context.CancellationToken);
        // Persist the freshly read page so the desktop can update its farm-list UI instantly after
        // the send, without paying for the extra navigations a full re-analyze would cost.
        await WriteFarmListsSnapshotAsync(context, refreshedOverview);
        var refreshedMatching = refreshedOverview
            .Where(item => item is not null && IsSelected(item))
            .OrderBy(item => item.RemainingSeconds is > 0 ? item.RemainingSeconds.Value : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var otherReadyLists = refreshedMatching
            .Where(item => !string.Equals(item.Name, ready.Name, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.RemainingSeconds is null or <= 0)
            .Select(item => item.Name)
            .ToList();

        int nextWaitSeconds;
        if (otherReadyLists.Count > 0)
        {
            context.Log($"Continuous farming: more ready lists found after send: {string.Join(", ", otherReadyLists)}. Waiting configured delay {dispatchDelaySeconds}s.");
            nextWaitSeconds = dispatchDelaySeconds;
        }
        else
        {
            nextWaitSeconds = refreshedMatching
                .Where(item => item.RemainingSeconds is > 0)
                .Min(item => item.RemainingSeconds) ?? Math.Max(1, remainingSeconds ?? dispatchDelaySeconds);
            context.Log($"Continuous farming: no additional list is ready. Shortest remaining time={nextWaitSeconds}s.");
        }

        context.Log($"Continuous farming becomes ready again in {nextWaitSeconds}s.");
        throw new InvalidOperationException($"Continuous farming cooldown active. queue_wait_seconds={Math.Max(1, nextWaitSeconds)}");
    }

    private static async Task ExecuteSendResourcesBetweenVillagesAsync(TaskExecutionContext context)
    {
        context.Log("send_resources_between_villages: starting.");
        var result = await context.Client.SendResourcesBetweenOwnVillagesAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("send_resources_between_villages", result);
    }

    private static async Task ExecuteSendReinforcementsBetweenVillagesAsync(TaskExecutionContext context)
    {
        context.Log("send_reinforcements_between_villages: starting.");
        var result = await context.Client.SendReinforcementsBetweenOwnVillagesAsync(context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("send_reinforcements_between_villages", result);
    }

    private static async Task ExecuteHeroManageAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ManageHeroAsync(
            context.Options.HeroMinHpForAdventure,
            context.Options.HeroAutoRevive,
            context.Options.HeroAutoAssignPoints,
            context.Options.HeroAutoUseOintments,
            context.Options.HeroStatPriority,
            context.Options.HeroAdventurePickOrder,
            context.Options.HeroHideModeEnabled,
            context.Options.HeroHideMode,
            context.Options.HeroHpRegenPerDayPercent,
            context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("hero_manage", result);
    }

    private static async Task ExecuteCollectTasksAsync(TaskExecutionContext context)
    {
        var result = await context.Client.CollectTaskRewardsAsync(context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteCollectDailyQuestsAsync(TaskExecutionContext context)
    {
        var result = await context.Client.CollectDailyQuestRewardsAsync(context.CancellationToken);
        context.Log(result);
    }

    private static void ThrowIfTaskBlocked(string taskName, string result)
    {
        if (!IsBlockedTaskResult(result))
        {
            return;
        }

        // Permanent blocks: the request can never succeed in its current form. Mark Failed
        // immediately rather than letting the worker burn the retry budget on it.
        if (IsPermanentlyBlockedTaskResult(result))
        {
            throw new TaskBlockedPermanentlyException($"Task '{taskName}' blocked permanently: {result}");
        }

        // Transient blocks with an explicit wait hint → defer without consuming retries.
        if (TryExtractQueueWaitSeconds(result, out var waitSeconds))
        {
            throw new TaskWaitException(waitSeconds, $"Task '{taskName}' waiting: {result}");
        }

        // Blocked but no wait hint — fall back to old behavior (counts toward MaxRetries).
        throw new InvalidOperationException($"Task '{taskName}' could not execute successfully: {result}");
    }

    private static void ThrowIfTroopsGroupBlocked(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        if (result.Contains("Smithy not found in this village", StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskBlockedPermanentlyException($"Task 'upgrade_troops_at_smithy' blocked permanently: troops_blocked=smithy_missing | {result}");
        }

        if (result.Contains("Smithy:", StringComparison.OrdinalIgnoreCase)
            && result.Contains("All done", StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskBlockedPermanentlyException($"Task 'upgrade_troops_at_smithy' blocked permanently: troops_blocked=all_done | {result}");
        }
    }

    internal static bool IsBlockedTaskResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }

        var value = result.ToLowerInvariant();
        return
            value.Contains(" blocked ")
            || value.Contains("blocked (")
            || value.Contains("queue_wait_seconds=")
            || value.Contains("cannot be built yet")
            || value.Contains("cannot be upgraded yet")
            || value.Contains("is not listed by the server")
            || value.Contains("cannot be built in slot")
            || value.Contains("reports max level reached");
    }

    private static bool IsPermanentlyBlockedTaskResult(string result)
    {
        var value = result.ToLowerInvariant();
        return
            value.Contains("reports max level reached")
            || value.Contains("is not listed by the server")
            || value.Contains("cannot be built in slot");
    }

    private static bool TryExtractQueueWaitSeconds(string result, out int seconds)
    {
        seconds = 0;
        const string token = "queue_wait_seconds=";
        var index = result.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + token.Length;
        var end = start;
        while (end < result.Length && (char.IsDigit(result[end]) || result[end] == '-'))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        if (!int.TryParse(result.AsSpan(start, end - start), out var parsed))
        {
            return false;
        }

        seconds = parsed;
        return true;
    }

    private static int ResolveQueueWaitDelaySeconds(BotOptions options, int waitSeconds)
    {
        if (waitSeconds <= 0)
        {
            return 1;
        }

        var mode = options.QueueWaitThresholdMode?.Trim();
        if (string.Equals(mode, "smart", StringComparison.OrdinalIgnoreCase))
        {
            return waitSeconds;
        }

        if (!int.TryParse(mode, out var thresholdSeconds) || thresholdSeconds < 0)
        {
            thresholdSeconds = 10;
        }

        thresholdSeconds = Math.Max(1, thresholdSeconds);
        return Math.Max(1, Math.Min(thresholdSeconds, waitSeconds));
    }

    private sealed record TaskExecutionContext(
        BotTaskRunner Runner,
        BotOptions Options,
        TravianClient Client,
        Action<string> Log,
        CancellationToken CancellationToken);

    private sealed record ClientLease(
        BrowserSession Session,
        TravianClient Client,
        bool KeepOpen);

    private static async Task TrySwitchToTargetVillageAsync(
        TravianClient client,
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken,
        string? explicitVillageName = null,
        string? explicitVillageUrl = null,
        bool skipFeatureRefresh = false)
    {
        var targetName = string.IsNullOrWhiteSpace(explicitVillageName) ? options.TargetVillageName : explicitVillageName;
        var targetUrl = string.IsNullOrWhiteSpace(explicitVillageUrl) ? options.TargetVillageUrl : explicitVillageUrl;
        if (string.IsNullOrWhiteSpace(targetName) && string.IsNullOrWhiteSpace(targetUrl))
        {
            return;
        }

        await client.SwitchToVillageAsync(targetName, targetUrl, cancellationToken, skipFeatureRefresh);
        var label = !string.IsNullOrWhiteSpace(targetName) ? targetName : targetUrl;
        log($"[village-switch:verbose] target village applied: {label}");
    }
}
