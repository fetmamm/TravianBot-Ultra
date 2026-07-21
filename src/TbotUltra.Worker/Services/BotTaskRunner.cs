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

public sealed partial class BotTaskRunner
{
    private enum BrowserStateSaveMode
    {
        Always,
        Skip
    }

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
            // Spends available hero attribute points using the configured stat priority.
            ["spend_hero_attribute_points"] = ExecuteSpendHeroAttributePointsAsync,
            // Walks through the Smithy and clicks every "Upgrade" button until none remain.
            ["upgrade_troops_at_smithy"] = ExecuteUpgradeTroopsAtSmithyAsync,
            // Builds troops from Barracks, Stable, or Workshop based on configured rules.
            ["build_troops"] = ExecuteBuildTroopsAsync,
            // Starts or tracks the Teutons brewery celebration.
            ["run_brewery_celebration"] = ExecuteRunBreweryCelebrationAsync,
            // Starts or tracks Town Hall celebrations.
            ["run_town_hall_celebration"] = ExecuteRunTownHallCelebrationAsync,
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
            // Official: watches the free +15% production bonus videos on the payment wizard Advantages tab.
            ["activate_production_bonus"] = ExecuteActivateProductionBonusAsync,
            // Official: reads the daily server-reset hour from the daily quests dialog (seeds +15% scheduling).
            ["read_daily_reset"] = ExecuteReadDailyResetAsync,
        };

    private readonly IAccountProvider _accountProvider;
    private readonly ProjectContext _projectContext;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly BulkMessageSentCacheStore _bulkMessageSentCacheStore;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly BrowserSessionGeneration _sessionGeneration = new();
    private BrowserSession? _sharedVisibleSession;
    private IPage? _sharedVisiblePage;
    private IPage? _travcoPage;
    private string? _sharedVisibleAccountName;
    private string? _sharedVisibleBaseUrl;
    // Proxy settings the shared browser was LAUNCHED with. Playwright proxy is a launch option and
    // cannot change on a running browser, so a proxy toggle must force a new session.
    private string? _sharedVisibleProxyFingerprint;
    // Session-scoped cache shared by every TravianClient created for the shared visible browser, so
    // feature signals (Plus/GoldClub/tribe) and the logged-in throttle survive across operations.
    private TravianSessionCache _sharedVisibleSessionCache = new();
    private int _browserClosedByUserSignal;

    public BotTaskRunner(IAccountProvider accountProvider, ProjectContext projectContext)
    {
        _accountProvider = accountProvider;
        _projectContext = projectContext;
        _accountAnalysisStore = new AccountAnalysisStore(projectContext.RootPath);
        _bulkMessageSentCacheStore = new BulkMessageSentCacheStore(projectContext.RootPath);
    }

    public Func<LobbyWorldSelectionRequest, CancellationToken, Task<string?>>? LobbyWorldSelectionRequested { get; set; }
    public Func<LobbyWorldServerResolution, CancellationToken, Task>? LobbyWorldServerResolved { get; set; }

    public static IReadOnlyList<string> RegisteredTaskNames => TaskHandlers.Keys.ToList();

    public async Task<IReadOnlyList<MapOasisEntry>> ScanMapOasesAsync(
        BotOptions options,
        bool includeOccupied,
        IReadOnlyCollection<string> selectedTypes,
        MapOasisScanRequest request,
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
                    using var trace = client.BeginBrowserTraceFlow(
                        null,
                        "scan-map-oases",
                        options.TargetVillageName,
                        "manual-ui-function");
                    try
                    {
                        result = await client.ScanMapOasesAsync(
                            includeOccupied,
                            selectedTypes,
                            request,
                            progress,
                            cancellationToken);
                        trace.Complete("success", $"count={result.Count}");
                    }
                    catch (OperationCanceledException)
                    {
                        trace.Complete("canceled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        trace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}");
                        throw;
                    }
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

    public async Task<BotTaskExecutionResult> ExecuteOnceAsync(
        BotOptions options,
        Action<string> log,
        IEnumerable<string>? tasksOverride = null,
        string? accountName = null,
        CancellationToken cancellationToken = default,
        string? traceRunId = null)
    {
        var tasks = tasksOverride?.ToList() ?? (options.LoopTasks is { Count: > 0 } configuredTasks ? configuredTasks : ["status"]);
        var taskResults = new List<BotTaskResult>();
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                var tickSw = System.Diagnostics.Stopwatch.StartNew();
                using var executionTrace = client.BeginBrowserTraceFlow(
                    traceRunId,
                    tasks.Count == 1 ? tasks[0] : "multi-task",
                    options.TargetVillageName,
                    "worker-execution");
                log($"[tick] starting — account='{client.AccountName}' server='{options.ServerName}' targetVillage='{options.TargetVillageName ?? "(default)"}'");
                log($"[tick] tasks ({tasks.Count}): {string.Join(", ", tasks)}");
                try
                {
                    await client.LoginAsync(cancellationToken);
                    await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                    var context = new TaskExecutionContext(this, options, client, log, cancellationToken, taskResults.Add);
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
                        using var taskTrace = client.BeginBrowserTraceFlow(
                            traceRunId,
                            taskName,
                            options.TargetVillageName,
                            "task-handler");
                        log($"[{taskName} STARTED] ({taskIndex}/{tasks.Count}) on '{client.AccountName}'");
                        try
                        {
                            await handler(context);
                            taskTrace.Complete("success");
                            log($"[{taskName} COMPLETED] in {taskSw.Elapsed.TotalSeconds:F1}s ({taskIndex}/{tasks.Count})");
                        }
                        catch (OperationCanceledException)
                        {
                            taskTrace.Complete("canceled");
                            log($"[{taskName} CANCELED] after {taskSw.Elapsed.TotalSeconds:F1}s ({taskIndex}/{tasks.Count})");
                            throw;
                        }
                        catch (TaskWaitException waitEx)
                        {
                            taskTrace.Complete("deferred", $"waitSeconds={waitEx.DelaySeconds}");
                            log($"[{taskName} DEFERRED] after {taskSw.Elapsed.TotalSeconds:F1}s — wait {waitEx.DelaySeconds}s: {waitEx.Message}");
                            throw;
                        }
                        catch (TaskBlockedPermanentlyException blockedEx)
                        {
                            taskTrace.Complete("blocked", blockedEx.Message);
                            log($"[{taskName} BLOCKED] after {taskSw.Elapsed.TotalSeconds:F1}s: {blockedEx.Message}");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            taskTrace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}");
                            if (BrowserFailureClassifier.IsTargetCrash(ex))
                            {
                                log($"[{taskName} DEFERRED] after {taskSw.Elapsed.TotalSeconds:F1}s: Chromium target crashed");
                            }
                            else
                            {
                                log($"[{taskName} FAILED] after {taskSw.Elapsed.TotalSeconds:F1}s: {ex.GetType().Name}: {ex.Message}");
                            }

                            throw;
                        }
                    }

                    log($"[tick] completed in {tickSw.Elapsed.TotalSeconds:F1}s ({tasks.Count} task(s)) on '{client.AccountName}'");
                    executionTrace.Complete("success");
                }
                catch (OperationCanceledException)
                {
                    executionTrace.Complete("canceled");
                    throw;
                }
                catch (TaskWaitException waitEx)
                {
                    executionTrace.Complete("deferred", $"waitSeconds={waitEx.DelaySeconds}");
                    throw;
                }
                catch (TaskBlockedPermanentlyException blockedEx)
                {
                    executionTrace.Complete("blocked", blockedEx.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    executionTrace.Complete("failed", $"{ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            });
        return new BotTaskExecutionResult(taskResults);
    }

    public async Task ShutdownAsync(Action<string>? log = null)
    {
        var hadActiveBrowserWork = _sharedVisibleSession is not null
            || _sharedVisiblePage is not null
            || _travcoPage is not null
            || _sessionGate.CurrentCount == 0;
        var invalidatedGeneration = _sessionGeneration.Invalidate();
        if (hadActiveBrowserWork)
        {
            log?.Invoke($"[browser-session] active browser shutdown invalidated session generation {invalidatedGeneration}.");
        }
        // A stuck operation (for example a hung navigation) can hold the session gate for
        // a long time. Shutdown/account switch must not hang behind it: after the timeout we close
        // the browser anyway, which makes the stuck operation fail fast with a target-closed error
        // and release the gate on its own.
        var gateAcquired = await _sessionGate.WaitAsync(TimeSpan.FromSeconds(15));
        if (!gateAcquired)
        {
            log?.Invoke("[browser-session] shutdown: session gate still held after 15s — force-closing the browser to unblock the running operation.");
        }

        try
        {
            if (_travcoPage is not null)
            {
                try
                {
                    await CloseTravcoPageAsync(_travcoPage, log);
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
                _sharedVisibleProxyFingerprint = null;
                _sharedVisibleSessionCache = new TravianSessionCache();
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _sessionGate.Release();
            }
        }
    }

    public async Task SaveBrowserStateAsync(Action<string>? log = null)
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_sharedVisibleSession is null)
            {
                log?.Invoke("[browser-session] state save skipped: no shared browser is open.");
                return;
            }

            await _sharedVisibleSession.SaveStateAsync();
            log?.Invoke("[browser-session] state saved before browser close.");
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
        Func<TravianClient, Task> action,
        BrowserStateSaveMode saveStateMode = BrowserStateSaveMode.Always)
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
            catch (Exception ex) when (lease.KeepOpen && BrowserFailureClassifier.IsTargetCrash(ex))
            {
                await InvalidateCrashedSharedSessionAsync(lease, log);
                throw;
            }
            finally
            {
                if (!lease.Invalidated)
                {
                    await FinalizeLeaseAsync(lease, log, saveStateMode);
                }
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
        var leaseGeneration = _sessionGeneration.Capture();
        var desiredBaseUrl = options.BaseUrl.TrimEnd('/');
        var desiredProxyFingerprint = account.ProxyEnabled ? $"on|{account.ProxyServer.Trim()}" : "off";
        if (account.NeverUseOwnIp
            && (!account.ProxyEnabled || !ProxyParser.TryBuild(account.ProxyServer, out _, out _)))
        {
            throw new InvalidOperationException(
                $"Account '{account.Name}' has 'Never use own IP address' enabled, but no valid proxy is configured. Browser startup blocked.");
        }

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

        if (_sharedVisibleSession is not null
            && !string.Equals(_sharedVisibleProxyFingerprint, desiredProxyFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            // Mask inline proxy credentials before logging.
            replaceReasons.Add($"proxy='{MaskProxyFingerprint(_sharedVisibleProxyFingerprint)}'->'{MaskProxyFingerprint(desiredProxyFingerprint)}'");
        }

        var mustReplaceSession =
            _sharedVisibleSession is null ||
            _sharedVisiblePage is null ||
            _sharedVisiblePage.IsClosed ||
            !string.Equals(_sharedVisibleAccountName, account.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_sharedVisibleBaseUrl, desiredBaseUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_sharedVisibleProxyFingerprint, desiredProxyFingerprint, StringComparison.OrdinalIgnoreCase);

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

            var session = new BrowserSession(options, account, _projectContext.RootPath, log: log);
            var page = await session.OpenPageAsync(cancellationToken);
            try
            {
                _sessionGeneration.ThrowIfStale(leaseGeneration);
            }
            catch
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception ex)
                {
                    log($"[browser-session] superseded session cleanup failed: {ex.Message}");
                }
                throw;
            }
            _sharedVisibleSession = session;
            _sharedVisiblePage = page;
            _sharedVisibleAccountName = account.Name;
            _sharedVisibleBaseUrl = desiredBaseUrl;
            _sharedVisibleProxyFingerprint = desiredProxyFingerprint;
            // Fresh browser/account => start a clean session cache so no stale signals carry over.
            _sharedVisibleSessionCache = CreateSeededSessionCache(account, options, log);
            log("Opened shared browser window.");
        }
        else
        {
            SeedStableAccountSignals(_sharedVisibleSessionCache, account, options, log);
        }

        await _sharedVisibleSession!.SetDetailedBrowserLoggingAsync(
            options.DetailedBrowserLoggingEnabled,
            cancellationToken);

        var sharedClient = CreateClient(_sharedVisiblePage!, options, account, interactive, log, _sharedVisibleSessionCache,
            setConsentDomainsAllowed: allowed => GetRequiredSharedVisibleSession().ConsentDomainsAllowed = allowed,
            cleanupAfterBonusVideoAsync: (page, ct) => GetRequiredSharedVisibleSession().CleanupAfterBonusVideoAsync(page, ct),
            runInIsolatedBonusVideoBrowserAsync: (action, ct) => GetRequiredSharedVisibleSession().RunInIsolatedBonusVideoBrowserAsync(action, ct),
            rotateAfterLobbyLoginAsync: (serverUrl, ct) => RotateSharedVisibleContextAfterLobbyLoginAsync(serverUrl, log, leaseGeneration, ct),
            browserTrace: GetRequiredSharedVisibleSession().BrowserTrace);
        return new ClientLease(_sharedVisibleSession!, sharedClient, true);
    }

    private BrowserSession GetRequiredSharedVisibleSession()
    {
        return _sharedVisibleSession
            ?? throw new InvalidOperationException("The shared browser session is not available.");
    }

    private async Task<IPage> RotateSharedVisibleContextAfterLobbyLoginAsync(
        string effectiveBaseUrl,
        Action<string> log,
        long leaseGeneration,
        CancellationToken cancellationToken)
    {
        var session = GetRequiredSharedVisibleSession();
        log("[browser-session] lobby SSO confirmed; rotating to a clean game context while keeping Chromium open.");
        _sharedVisiblePage = null;
        _travcoPage = null;

        try
        {
            var cleanPage = await session.RotateMainContextFromSavedStateAsync(effectiveBaseUrl, cancellationToken);
            try
            {
                _sessionGeneration.ThrowIfStale(leaseGeneration);
            }
            catch
            {
                try
                {
                    await cleanPage.CloseAsync();
                }
                catch (Exception ex)
                {
                    log($"[browser-session] superseded post-lobby page cleanup failed: {ex.Message}");
                }
                throw;
            }
            _sharedVisiblePage = cleanPage;
            log("[browser-session] clean post-lobby game context opened in the existing Chromium process.");
            return cleanPage;
        }
        catch
        {
            _sharedVisiblePage = null;
            throw;
        }
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

    // Fingerprint format is "off" or "on|<server>"; the server part may carry inline credentials.
    private static string MaskProxyFingerprint(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return "-";
        }

        return fingerprint.StartsWith("on|", StringComparison.OrdinalIgnoreCase)
            ? $"on|{ProxyParser.MaskForLog(fingerprint[3..])}"
            : fingerprint;
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
        TravianSessionCache? sessionCache = null,
        Action<bool>? setConsentDomainsAllowed = null,
        Func<IPage, CancellationToken, Task>? cleanupAfterBonusVideoAsync = null,
        Func<Func<IPage, CancellationToken, Task<string>>, CancellationToken, Task<string>>? runInIsolatedBonusVideoBrowserAsync = null,
        Func<string, CancellationToken, Task<IPage>>? rotateAfterLobbyLoginAsync = null,
        BrowserTraceLogger? browserTrace = null)
    {
        return new TravianClient(
            page,
            options,
            account,
            interactive: interactive,
            browserVisible: true,
            projectRoot: _projectContext.RootPath,
            statusCallback: log,
            sessionCache: sessionCache,
            setConsentDomainsAllowed: setConsentDomainsAllowed,
            cleanupAfterBonusVideoAsync: cleanupAfterBonusVideoAsync,
            runInIsolatedBonusVideoBrowserAsync: runInIsolatedBonusVideoBrowserAsync,
            rotateAfterLobbyLoginAsync: rotateAfterLobbyLoginAsync,
            lobbyWorldSelectionRequested: LobbyWorldSelectionRequested,
            lobbyWorldServerResolved: async (resolution, cancellationToken) =>
            {
                if (LobbyWorldServerResolved is not null)
                {
                    await LobbyWorldServerResolved(resolution, cancellationToken);
                }

                if (string.Equals(_sharedVisibleAccountName, resolution.AccountName, StringComparison.OrdinalIgnoreCase))
                {
                    _sharedVisibleBaseUrl = resolution.ServerUrl.TrimEnd('/');
                    log($"[browser-session] active server identity updated to '{new Uri(_sharedVisibleBaseUrl).Host}' after verified lobby correction.");
                }
            },
            browserTrace: browserTrace);
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

        if (IsKnownTribe(analysis?.Tribe) && !IsKnownTribe(sessionCache.AccountTribe))
        {
            sessionCache.AccountTribe = analysis!.Tribe;
            sessionCache.CachedTribePlusAt = DateTimeOffset.UtcNow;
            log($"[cache] tribe='{analysis.Tribe}' loaded for '{account.Name}'.");
        }

        if (analysis?.GoldClubEnabled == true && sessionCache.CachedGoldClubEnabled != true)
        {
            sessionCache.CachedGoldClubEnabled = true;
            sessionCache.CachedTribePlusAt = DateTimeOffset.UtcNow;
            log($"[cache] goldclub=True loaded for '{account.Name}'.");
        }

        // Seed only when the session has no village list yet — this method runs on EVERY lease of the
        // shared browser, not just on login. Re-seeding overwrote the live sidebar-merged list with the
        // stale on-disk analysis and reset CachedVillagesAt, forcing a sidebar re-read per lease (49
        // misses vs 12 hits in one session) and logging this line 272 times. The tribe/goldclub blocks
        // above are guarded the same way, which is why they appear once.
        if (analysis?.Villages is { Count: > 0 } villages && sessionCache.CachedVillages is not { Count: > 0 })
        {
            sessionCache.CachedVillages = villages.Select(village => village with { }).ToList();
            // Force one cheap sidebar merge after login. It detects founded/renamed villages while
            // retaining persisted coordinates/population, without opening the profile page.
            sessionCache.CachedVillagesAt = DateTimeOffset.MinValue;
            sessionCache.CachedVillagesPopulationAt = villages.Any(village => village.Population.HasValue)
                ? analysis.AnalyzedAtUtc
                : DateTimeOffset.MinValue;
            log($"[cache] village snapshot ({villages.Count}) loaded for '{account.Name}'; live sidebar merge pending.");
        }
    }

    private async Task FinalizeLeaseAsync(ClientLease lease, Action<string> log, BrowserStateSaveMode saveStateMode = BrowserStateSaveMode.Always)
    {
        var activeSession = lease.KeepOpen
            ? _sharedVisibleSession ?? lease.Session
            : lease.Session;
        if (saveStateMode == BrowserStateSaveMode.Always)
        {
            try
            {
                await activeSession.SaveStateAsync();
            }
            catch (Exception ex)
            {
                log($"Could not save browser state: {ex.Message}");
            }
        }

        if (!lease.KeepOpen)
        {
            await activeSession.DisposeAsync();
        }
    }

    private async Task InvalidateCrashedSharedSessionAsync(ClientLease lease, Action<string> log)
    {
        lease.Invalidated = true;
        log("[browser-session] Chromium target crashed. Discarding shared session; next operation will open a fresh browser.");

        var activeSession = lease.KeepOpen
            ? _sharedVisibleSession ?? lease.Session
            : lease.Session;
        try
        {
            await activeSession.DisposeAsync();
        }
        catch (Exception ex)
        {
            log($"Crashed browser cleanup failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_sharedVisibleSession, activeSession))
            {
                _sharedVisibleSession = null;
                _sharedVisiblePage = null;
                _sharedVisibleAccountName = null;
                _sharedVisibleBaseUrl = null;
                _sharedVisibleProxyFingerprint = null;
                _travcoPage = null;
                _sharedVisibleSessionCache = new TravianSessionCache();
            }
        }
    }

    private sealed record TaskExecutionContext(
        BotTaskRunner Runner,
        BotOptions Options,
        TravianClient Client,
        Action<string> Log,
        CancellationToken CancellationToken,
        Action<BotTaskResult> RecordResult)
    {
        public void RecordTaskResult(string taskName, string? result) =>
            RecordResult(new BotTaskResult(
                taskName,
                result,
                ClassifyConstructionTaskResult(taskName, result)));
    }

    private sealed record ClientLease(
        BrowserSession Session,
        TravianClient Client,
        bool KeepOpen)
    {
        public bool Invalidated { get; set; }
    }

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
