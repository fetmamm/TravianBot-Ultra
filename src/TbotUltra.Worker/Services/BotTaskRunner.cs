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
        };

    private readonly IAccountProvider _accountProvider;
    private readonly ProjectContext _projectContext;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly BulkMessageSentCacheStore _bulkMessageSentCacheStore;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
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

    public async Task<BotTaskExecutionResult> ExecuteOnceAsync(
        BotOptions options,
        Action<string> log,
        IEnumerable<string>? tasksOverride = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
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
                log($"[tick] starting — account='{client.AccountName}' server='{options.ServerName}' targetVillage='{options.TargetVillageName ?? "(default)"}'");
                log($"[tick] tasks ({tasks.Count}): {string.Join(", ", tasks)}");

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
            });
        return new BotTaskExecutionResult(taskResults);
    }

    public async Task ShutdownAsync(Action<string>? log = null)
    {
        // A stuck operation (unsolved captcha pause, hung navigation) can hold the session gate for
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
        var desiredBaseUrl = options.BaseUrl.TrimEnd('/');
        var desiredProxyFingerprint = account.ProxyEnabled ? $"on|{account.ProxyServer.Trim()}" : "off";
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

        var sharedVisibleSession = _sharedVisibleSession!;
        var sharedClient = CreateClient(_sharedVisiblePage!, options, account, interactive, log, _sharedVisibleSessionCache,
            setConsentDomainsAllowed: allowed => sharedVisibleSession.ConsentDomainsAllowed = allowed,
            cleanupAfterBonusVideoAsync: sharedVisibleSession.CleanupAfterBonusVideoAsync,
            runInIsolatedBonusVideoBrowserAsync: (action, ct) => sharedVisibleSession.RunInIsolatedBonusVideoBrowserAsync(action, ct));
        return new ClientLease(sharedVisibleSession, sharedClient, true);
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
        Func<Func<IPage, CancellationToken, Task<string>>, CancellationToken, Task<string>>? runInIsolatedBonusVideoBrowserAsync = null)
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
            runInIsolatedBonusVideoBrowserAsync: runInIsolatedBonusVideoBrowserAsync);
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

    private async Task FinalizeLeaseAsync(ClientLease lease, Action<string> log, BrowserStateSaveMode saveStateMode = BrowserStateSaveMode.Always)
    {
        if (saveStateMode == BrowserStateSaveMode.Always)
        {
            try
            {
                await lease.Session.SaveStateAsync();
            }
            catch (Exception ex)
            {
                log($"Could not save browser state: {ex.Message}");
            }
        }

        if (!lease.KeepOpen)
        {
            await lease.Session.DisposeAsync();
        }
    }

    private async Task InvalidateCrashedSharedSessionAsync(ClientLease lease, Action<string> log)
    {
        lease.Invalidated = true;
        log("[browser-session] Chromium target crashed. Discarding shared session; next operation will open a fresh browser.");

        try
        {
            await lease.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            log($"Crashed browser cleanup failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_sharedVisibleSession, lease.Session))
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
