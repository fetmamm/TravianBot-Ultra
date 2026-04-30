using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
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
            // Performs a full analysis of the account (tribe, gold club, building catalog) and saves it.
            ["account_full_analysis"] = ExecuteAccountFullAnalysisAsync,
            // Demolishes a specified building to a target level.
            ["demolish_building_to_level"] = ExecuteDemolishBuildingToLevelAsync,
            // Manages hero actions: revives if dead, allocates points, and sends on adventures if HP allows.
            ["hero_manage"] = ExecuteHeroManageAsync,
            // Sends the hero on the first available adventure if hero is in home village.
            ["hero_send_adventure"] = ExecuteHeroSendAdventureAsync,
        };

    private readonly IAccountProvider _accountProvider;
    private readonly ProjectContext _projectContext;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly ICaptchaAutoSolver _captchaAutoSolver;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private BrowserSession? _sharedVisibleSession;
    private IPage? _sharedVisiblePage;
    private string? _sharedVisibleAccountName;
    private string? _sharedVisibleBaseUrl;
    private int _browserClosedByUserSignal;

    public BotTaskRunner(IAccountProvider accountProvider, ProjectContext projectContext, ICaptchaAutoSolver captchaAutoSolver)
    {
        _accountProvider = accountProvider;
        _projectContext = projectContext;
        _captchaAutoSolver = captchaAutoSolver;
        _accountAnalysisStore = new AccountAnalysisStore(projectContext.RootPath);
    }

    public static IReadOnlyList<string> RegisteredTaskNames => TaskHandlers.Keys.ToList();

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
                log($"Starting tick for server {options.ServerName}.");
                log($"Tasks: {string.Join(",", tasks)}");

                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                var context = new TaskExecutionContext(this, options, client, log, cancellationToken);
                foreach (var taskName in tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TaskCatalog.IsAllowed(taskName))
                    {
                        log($"Task '{taskName}' is not allowed.");
                        continue;
                    }

                    if (!TaskHandlers.TryGetValue(taskName, out var handler))
                    {
                        log($"Task '{taskName}' is allowed but not implemented yet.");
                        continue;
                    }

                    log($"[{taskName} STARTED]");
                    await handler(context);
                    log($"[{taskName} COMPLETED]");
                }
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
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                log("Login completed and browser session saved.");
                if (!options.Headless)
                {
                    log("Browser stays open (headless is disabled).");
                }
            });
    }

    public async Task<AccountSnapshot> AnalyzeProfileAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        AccountSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Analyzing profile for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                snapshot = await client.AnalyzeProfileAsync(cancellationToken);
            });
        return snapshot ?? throw new InvalidOperationException("Could not analyze profile.");
    }

    public async Task<bool> ReadAndPersistGoldClubStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var account = _accountProvider.LoadAccount(accountName);
        _accountAnalysisStore.TryLoad(account.Name, out var existing);
        var detectedGoldClubEnabled = false;
        var serverUrl = options.BaseUrl.TrimEnd('/');
        var tribe = existing?.Tribe ?? "Unknown";

        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                detectedGoldClubEnabled = await client.ReadGoldClubStatusAsync(cancellationToken);
                serverUrl = client.ServerUrl;
                if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = await client.ReadAccountSnapshotAsync(cancellationToken);
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
            BuildingCatalog: existing?.BuildingCatalog ?? []);

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
                result = await client.AddFarmsFromNatarsAsync(farmListName, troopType, troopCount, requestedCount, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not add farms from Natars profile.");
    }

    public async Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh = false,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
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
        int troopCount,
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
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                status = await client.ReadVillageResourceStatusAsync(cancellationToken);
            });

        return status ?? throw new InvalidOperationException("Could not read village resource status.");
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
                count = await client.RefreshAdventureCountAsync(cancellationToken);
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
                    $"Hero attributes: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}.");
            });

        return snapshot ?? throw new InvalidOperationException("Could not read hero attributes.");
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
                log(changed ? "Messages marked as read." : "No unread messages to mark as read.");
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
                log(changed ? "Reports marked as read." : "No unread reports to mark as read.");
            });
    }

    public async Task ShutdownAsync(Action<string>? log = null)
    {
        await _sessionGate.WaitAsync();
        try
        {
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
            }
        }
        finally
        {
            _sessionGate.Release();
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
            var session = new BrowserSession(options, account, _projectContext.RootPath);
            var page = await session.OpenPageAsync(cancellationToken);
            var client = CreateClient(page, options, account, interactive, log);
            return new ClientLease(session, client, false);
        }

        var desiredBaseUrl = options.BaseUrl.TrimEnd('/');
        var mustReplaceSession =
            _sharedVisibleSession is null ||
            _sharedVisiblePage is null ||
            _sharedVisiblePage.IsClosed ||
            !string.Equals(_sharedVisibleAccountName, account.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_sharedVisibleBaseUrl, desiredBaseUrl, StringComparison.OrdinalIgnoreCase);

        if (_sharedVisiblePage is not null && _sharedVisiblePage.IsClosed)
        {
            Interlocked.Exchange(ref _browserClosedByUserSignal, 1);
            log("Shared browser window was closed. A new browser session will be created.");
        }

        if (mustReplaceSession)
        {
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

            var session = new BrowserSession(options, account, _projectContext.RootPath, headlessOverride: false);
            var page = await session.OpenPageAsync(cancellationToken);
            _sharedVisibleSession = session;
            _sharedVisiblePage = page;
            _sharedVisibleAccountName = account.Name;
            _sharedVisibleBaseUrl = desiredBaseUrl;
            log("Opened shared browser window.");
        }

        var sharedClient = CreateClient(_sharedVisiblePage!, options, account, interactive, log);
        return new ClientLease(_sharedVisibleSession!, sharedClient, true);
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
        Action<string> log)
    {
        return new TravianClient(
            page,
            options,
            account,
            interactive: interactive,
            browserVisible: !options.Headless,
            projectRoot: _projectContext.RootPath,
            captchaAutoSolver: _captchaAutoSolver,
            statusCallback: log);
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
        context.Log($"All villages scanned. StatusCount={statuses.Count}");
    }

    private static async Task ExecuteAccountSnapshotAsync(TaskExecutionContext context)
    {
        var snapshot = await context.Client.ReadAccountSnapshotAsync(context.CancellationToken);
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

    private static async Task ExecuteAccountFullAnalysisAsync(TaskExecutionContext context)
    {
        var analysis = await context.Client.ReadAccountAnalysisSnapshotAsync(context.CancellationToken);
        var completed = analysis with
        {
            SchemaVersion = AccountAnalysisConstants.CurrentSchemaVersion,
            AccountName = context.Client.AccountName,
            ServerUrl = context.Client.ServerUrl,
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
        };

        context.Runner._accountAnalysisStore.Save(completed);
        context.Log($"Account analysis saved for '{completed.AccountName}'. Tribe={completed.Tribe}, GoldClub={completed.GoldClubEnabled}, Catalog={completed.BuildingCatalog.Count}.");
    }

    private static async Task ExecuteLoadBuildingsSnapshotAsync(TaskExecutionContext context)
    {
        var status = await context.Client.ReadVillageStatusAsync(context.CancellationToken);
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
            buildings = status.Buildings.Select(building => new
            {
                slotId = building.SlotId,
                name = building.Name,
                level = building.Level,
                url = building.Url,
                gid = building.Gid,
            }).ToList(),
        };

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
        context.Log($"Loaded {status.Buildings.Count} building slots. Snapshot saved at {outputPath}.");
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

    private static async Task ExecuteHeroManageAsync(TaskExecutionContext context)
    {
        var result = await context.Client.ManageHeroAsync(
            context.Options.HeroMinHpForAdventure,
            context.Options.HeroAutoRevive,
            context.Options.HeroStatPriority,
            context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteHeroSendAdventureAsync(TaskExecutionContext context)
    {
        var result = await context.Client.SendHeroOnAdventureAsync(context.CancellationToken);
        context.Log(result.Message);

        if (result.SecondsUntilReturn is int waitSeconds && waitSeconds > 0)
        {
            var boundedWaitSeconds = Math.Min(waitSeconds, 12 * 60 * 60);
            context.Log($"Hero adventure wait: {boundedWaitSeconds}s.");
            await Task.Delay(TimeSpan.FromSeconds(boundedWaitSeconds), context.CancellationToken);
        }
    }

    private static void ThrowIfTaskBlocked(string taskName, string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var value = result.ToLowerInvariant();
        var isBlocked =
            value.Contains(" blocked ")
            || value.Contains("blocked (")
            || value.Contains("cannot be built yet")
            || value.Contains("cannot be upgraded yet")
            || value.Contains("is not listed by the server")
            || value.Contains("cannot be built in slot")
            || value.Contains("reports max level reached");

        if (!isBlocked)
        {
            return;
        }

        throw new InvalidOperationException($"Task '{taskName}' could not execute successfully: {result}");
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
        string? explicitVillageUrl = null)
    {
        var targetName = string.IsNullOrWhiteSpace(explicitVillageName) ? options.TargetVillageName : explicitVillageName;
        var targetUrl = string.IsNullOrWhiteSpace(explicitVillageUrl) ? options.TargetVillageUrl : explicitVillageUrl;
        if (string.IsNullOrWhiteSpace(targetName) && string.IsNullOrWhiteSpace(targetUrl))
        {
            return;
        }

        await client.SwitchToVillageAsync(targetName, targetUrl, cancellationToken);
        var label = !string.IsNullOrWhiteSpace(targetName) ? targetName : targetUrl;
        log($"Target village applied: {label}");
    }
}
