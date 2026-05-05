using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int MaxFarmsPerFarmList = 120;
    private const double ManualFarmingMinimumTroopRatio = 0.5d;
    private const int ManualFarmingMaxConsecutiveLowTroopSkips = 3;
    private const int ManualFarmingNoTroopsRetryAttempts = 3;
    private const int ManualFarmingNoTroopsRetryWaitSeconds = 10;
    private static readonly string[] CaptchaDetectionSelectors =
    [
        "input[name*='captcha' i]",
        "input[id*='captcha' i]",
        "input[placeholder*='captcha' i]",
        "img[src*='captcha' i]",
        "iframe[src*='captcha' i]",
        "iframe[src*='recaptcha' i]",
        "iframe[src*='hcaptcha' i]",
        "iframe[src*='challenges.cloudflare.com' i]",
        ".g-recaptcha",
        ".h-captcha",
        ".cf-turnstile",
        "#cf-challenge-running",
        "[class*='captcha' i]",
        "[id*='captcha' i]",
    ];
    private readonly IPage _page;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool _interactive;
    private readonly bool _browserVisible;
    private readonly string _projectRoot;
    private readonly string _capitalCachePath;
    private readonly NatarFarmCacheStore _natarFarmCacheStore;
    private readonly ICaptchaAutoSolver? _captchaAutoSolver;
    private readonly Action<string>? _statusCallback;
    private DateTimeOffset? _serverTimeUtc;
    private DateTimeOffset _lastManualVerificationScreenshotAt = DateTimeOffset.MinValue;
    private string? _cachedTribe;
    private bool? _cachedTravianPlusActive;
    private DateTimeOffset _cachedTribePlusAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan TribePlusCacheTtl = TimeSpan.FromMinutes(10);
    private bool? _cachedGoldClubEnabled;
    private string? _sessionTribe;

    private sealed class CaptchaClipRegion
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    private sealed record UiSyncVillage(string Name, string? Url, bool? IsCapital);
    private sealed record UiSyncSnapshot(int? Gold, int? Silver, string ActiveVillage, IReadOnlyList<UiSyncVillage> Villages);

    // Session-level cache for the villages list. Spieler.php is expensive to load and the data
    // changes rarely, so we share one read across LoginAsync, SwitchToVillageAsync and status reads.
    private static readonly TimeSpan VillagesCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EnsureLoggedInMinInterval = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan UiSyncMinInterval = TimeSpan.FromSeconds(20);
    private List<Village>? _cachedVillages;
    private DateTimeOffset _cachedVillagesAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEnsureLoggedInAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUiSyncAt = DateTimeOffset.MinValue;
    private bool _lastEnsureLoggedInSucceeded;
    private static readonly object NatarCacheSync = new();
    private static readonly Dictionary<string, List<NatarCoordinateJs>> CachedNatarCoordinatesByAccount = new(StringComparer.OrdinalIgnoreCase);

    public TravianClient(
        IPage page,
        BotOptions config,
        AccountOptions account,
        bool interactive = true,
        bool browserVisible = true,
        string? projectRoot = null,
        ICaptchaAutoSolver? captchaAutoSolver = null,
        Action<string>? statusCallback = null)
    {
        _page = page;
        _config = config;
        _account = account;
        _interactive = interactive;
        _browserVisible = browserVisible;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? Directory.GetCurrentDirectory()
            : projectRoot;
        _capitalCachePath = Path.Combine(_projectRoot, "config", "cache", "capital-state.json");
        _natarFarmCacheStore = new NatarFarmCacheStore(_projectRoot);
        _captchaAutoSolver = captchaAutoSolver;
        _statusCallback = statusCallback;
    }

    public string AccountName => _account.Name;
    public string ServerUrl => _config.BaseUrl.TrimEnd('/');

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        Notify("[LoginAsync] started");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before login.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }


        var loggedInFromCurrentPage = await TryLoginUsingCurrentPageAsync(cancellationToken);
        if (loggedInFromCurrentPage)
        {
            Notify("Login successful using current page.");
            // Behövs inte, göra senare.
            //await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }

        await GotoAsync(_config.LoginPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on the login page.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            Notify("Login successful after navigating to login page.");
            // Behövs inte, göra senare.
            //await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }

        await FillFirstAvailableAsync(Selectors.LoginUsernameField, _account.Username, cancellationToken);
        await FillFirstAvailableAsync(Selectors.LoginPasswordField, _account.Password, cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            var screenshotPath = await CaptureManualVerificationScreenshotAsync("login-page", cancellationToken);
            Notify("Captcha or manual login step detected.");
            if (await TrySolveCaptchaAutomaticallyAsync("login-page", screenshotPath, cancellationToken))
            {
                var loggedInAfterAutoSolve = await WaitUntilLoggedInAsync(cancellationToken);
                if (loggedInAfterAutoSolve)
                {
                    Notify("Login successful after captcha auto-solve.");
                    // Behövs inte, göra senare.
                    //await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
                    await RefreshAccountFeatureSignalsAsync(cancellationToken);
                    return;
                }
            }

            if (!_browserVisible)
            {
                throw new ManualVerificationRequiredException(
                    "Captcha/manual verification appeared while running headless.");
            }

            if (_interactive)
            {
                Console.WriteLine("Complete login manually in browser, then press Enter here.");
                Console.ReadLine();
            }
        }
        else
        {
            await ClickLoginButtonAsync(cancellationToken);
        }

        var loggedIn = await WaitUntilLoggedInAsync(cancellationToken);
        if (!loggedIn)
        {
            throw new InvalidOperationException("Login did not complete successfully.");
        }
        Notify("Login successful using other method...");
        // Behövs inte, göra senare.
        //await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
        await RefreshAccountFeatureSignalsAsync(cancellationToken);
    }

    public async Task RefreshAccountFeatureSignalsAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        try
        {
            var plus = await IsTravianPlusActiveAsync(cancellationToken);
            if (_cachedTravianPlusActive != plus)
            {
                _cachedTravianPlusActive = plus;
                _cachedTribePlusAt = DateTimeOffset.UtcNow;
            }
            Notify($"[plus] active={plus}");
        }
        catch (Exception ex)
        {
            Notify($"Plus status check failed: {ex.Message}");
        }

        // Gold Club is monotonic — once true within a session it cannot revert. Skip re-checks once latched.
        if (_cachedGoldClubEnabled == true)
        {
            Notify("[goldclub] active=True");
        }
        else
        {
            try
            {
                var gold = await ReadGoldClubEnabledAsync(cancellationToken);
                _cachedGoldClubEnabled = gold;
                Notify($"[goldclub] active={gold}");
            }
            catch (Exception ex)
            {
                Notify($"Gold Club status check failed: {ex.Message}");
            }
        }

        if (_sessionTribe is null)
        {
            try
            {
                var tribe = await ReadTribeAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(tribe) && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    _sessionTribe = tribe;
                    _cachedTribe = tribe;
                    _cachedTribePlusAt = DateTimeOffset.UtcNow;
                    Notify($"[tribe] {tribe}");
                }
            }
            catch (Exception ex)
            {
                Notify($"Tribe detection failed: {ex.Message}");
            }
        }
    }

    private async Task<bool> TryLoginUsingCurrentPageAsync(CancellationToken cancellationToken)
    {
        var hasUsernameField = await HasAnySelectorAsync(Selectors.LoginUsernameField);
        var hasPasswordField = await HasAnySelectorAsync(Selectors.LoginPasswordField);

        if (!hasUsernameField || !hasPasswordField)
        {
            return false;
        }

        Notify("Login form detected on current page. Trying login here first.");

        await FillFirstAvailableAsync(Selectors.LoginUsernameField, _account.Username, cancellationToken);
        await FillFirstAvailableAsync(Selectors.LoginPasswordField, _account.Password, cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            var screenshotPath = await CaptureManualVerificationScreenshotAsync("login-current-page", cancellationToken);
            Notify("Captcha or manual login step detected.");
            if (await TrySolveCaptchaAutomaticallyAsync("login-current-page", screenshotPath, cancellationToken))
            {
                var autoSolvedLoggedIn = await WaitUntilLoggedInAsync(cancellationToken);
                if (autoSolvedLoggedIn)
                {
                    Notify("Login completed from current page after captcha auto-solve.");
                    return autoSolvedLoggedIn;
                }
            }

            if (!_browserVisible)
            {
                throw new ManualVerificationRequiredException(
                    "Captcha/manual verification appeared while running headless.");
            }
        }
        else
        {
            await ClickLoginButtonAsync(cancellationToken);
        }

        var loggedIn = await WaitUntilLoggedInAsync(cancellationToken);
        if (loggedIn)
        {
            Notify("Login completed from current page.");
        }

        return loggedIn;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        Notify("[LogoutAsync] started");
        _sessionTribe = null;
        _cachedTribe = null;
        _cachedGoldClubEnabled = null;
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before logout.", cancellationToken);
        if (!await IsLoggedInAsync())
        {
            Notify("Already logged out.");
            return;
        }

        var clicked = await TryClickFirstAsync(Selectors.LogoutTriggers, cancellationToken);

        if (!clicked)
        {
            foreach (var candidatePath in Paths.LogoutCandidates)
            {
                await GotoAsync(candidatePath, cancellationToken);
                if (!await IsLoggedInAsync())
                {
                    Notify($"Logged out by navigation to {candidatePath}.");
                    return;
                }
            }
        }

        for (var i = 0; i < 6; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsLoggedInAsync())
            {
                Notify("Logged out successfully.");
                return;
            }

            await Task.Delay(350, cancellationToken);
        }

        throw new InvalidOperationException("Logout did not complete successfully.");
    }

    public async Task<VillageStatus> ReadVillageStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadVillageStatusAsync started");
        if (!IsCurrentUrlForPath(_config.VillageOverviewPath))
        {
            await GotoAsync(_config.VillageOverviewPath, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the village overview.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        return await ReadCurrentVillageStatusAsync(cancellationToken);
    }

    public async Task<bool> CheckLoggedInAsync(CancellationToken cancellationToken = default)
    {
        Notify("CheckLoggedInAsync started");
        cancellationToken.ThrowIfCancellationRequested();
        return await IsLoggedInAsync();
    }

    public async Task<AccountSnapshot> ReadAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadAccountSnapshotAsync started");
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading account info.", cancellationToken);
        await EnsureLoggedInAsync();

        var villages = await ReadVillagesAsync(cancellationToken);
        return new AccountSnapshot(
            Tribe: await ReadTribeAsync(cancellationToken),
            ActiveVillage: await ReadActiveVillageNameAsync(cancellationToken),
            VillageCount: villages.Count,
            Villages: villages,
            ServerTimeUtc: _serverTimeUtc);
    }

public async Task<AccountAnalysisSnapshot> ReadAccountAnalysisSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadAccountAnalysisSnapshotAsync started");
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading account analysis.", cancellationToken);
        await EnsureLoggedInAsync();
        await RefreshCapitalStateForActiveVillageAsync(cancellationToken);

        var tribe = await ReadTribeAsync(cancellationToken);
        var goldClubEnabled = await ReadGoldClubEnabledAsync(cancellationToken);
        var catalog = BuildingCatalogService.GetCatalogForTribe(tribe);

        return new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: _account.Name,
            ServerUrl: _config.BaseUrl.TrimEnd('/'),
            Tribe: tribe,
            GoldClubEnabled: goldClubEnabled,
            BuildingCatalog: catalog);
    }

    public async Task<bool> ReadGoldClubStatusAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        return await ReadGoldClubEnabledAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();

        var goldClubEnabled = await ReadGoldClubEnabledAsync(cancellationToken);
        if (!goldClubEnabled)
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var rows = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
        Notify($"Farm lists read: {rows.Count} list(s).");
        return rows;
    }

    public async Task<int?> SendFarmListNowAsync(string farmListName, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await WaitForDispatchLimitToClearAsync(cancellationToken);

        var clicked = await TryClickFarmListSendNowAsync(farmListName, cancellationToken);
        if (!clicked)
        {
            throw new InvalidOperationException($"Could not find clickable Start Raid button for farm list '{farmListName}'.");
        }

        await Task.Delay(250, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after sending farm list.", cancellationToken);
        await TryClickCaptchaSuccessDialogOkAsync(cancellationToken);
        var remaining = await ReadFarmListTimerSecondsByNameAsync(farmListName, cancellationToken);
        Notify($"Farm list '{farmListName}' sent. Timer={(remaining is > 0 ? FormatDuration(remaining.Value) : "Ready")}.");
        return remaining;
    }

    public async Task<FarmAddResult> AddSingleFarmFromNatarsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        var coordinate = await ReadFirstNatarFarmCoordinateAsync(cancellationToken);
        if (coordinate is null || coordinate.X is null || coordinate.Y is null)
        {
            throw new InvalidOperationException("Could not read any Natar farm coordinates from Natars profile.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var lid = await TryResolveFarmListSlotIdByNameAsync(farmListName, cancellationToken);
        if (string.IsNullOrWhiteSpace(lid))
        {
            throw new InvalidOperationException($"Could not find farm list '{farmListName}' on farm page.");
        }

        await GotoAsync(Paths.FarmListBySlotId(lid), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Add Raid form.", cancellationToken);
        await EnsureLoggedInAsync();

        var saveOutcome = await TryFillAddRaidFormAndSaveAsync(
            farmListName,
            troopType.Trim(),
            troopCount,
            coordinate.X.Value,
            coordinate.Y.Value,
            cancellationToken);
        if (saveOutcome == AddRaidSaveOutcome.Failed)
        {
            throw new InvalidOperationException("Could not fill Add Raid form or click Save.");
        }
        if (saveOutcome == AddRaidSaveOutcome.AlreadyInList)
        {
            throw new InvalidOperationException("This village is already in the selected farm list.");
        }
        Notify($"Added 1 farm to '{farmListName}' at ({coordinate.X}|{coordinate.Y}) with {troopCount} {troopType}.");
        return new FarmAddResult(farmListName, coordinate.X.Value, coordinate.Y.Value, troopType.Trim(), troopCount);
    }

    public async Task<FarmAddBatchResult> AddFarmsFromNatarsAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        if (requestedCount <= 0)
        {
            throw new InvalidOperationException("Requested farm count must be greater than 0.");
        }

        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var lid = await TryResolveFarmListSlotIdByNameAsync(farmListName, cancellationToken);
        if (string.IsNullOrWhiteSpace(lid))
        {
            throw new InvalidOperationException($"Could not find farm list '{farmListName}' on farm page.");
        }

        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken);
        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("Could not read any 'Natar farm village' coordinates from Natars profile.");
        }

        var maxAttempts = Math.Min(requestedCount, coordinates.Count);
        Notify($"Starting add farms batch: requested={requestedCount}, available={coordinates.Count}, attempts={maxAttempts}.");
        var added = 0;
        var alreadyInList = 0;
        var failed = 0;
        var attempted = 0;
        for (var i = 0; i < maxAttempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinate = coordinates[i];
            attempted++;
            var stepPrefix = $"[{attempted}/{maxAttempts}]";

            await GotoAsync(Paths.FarmListBySlotId(lid), cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Add Raid form.", cancellationToken);
            await EnsureLoggedInAsync();

            var saveOutcome = await TryFillAddRaidFormAndSaveAsync(
                farmListName,
                troopType.Trim(),
                troopCount,
                coordinate.X ?? 0,
                coordinate.Y ?? 0,
                cancellationToken);

            if (saveOutcome == AddRaidSaveOutcome.Added)
            {
                added++;
                Notify($"{stepPrefix} Added farm ({coordinate.X}|{coordinate.Y}) to '{farmListName}'.");
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.AlreadyInList)
            {
                alreadyInList++;
                Notify($"{stepPrefix} Farm ({coordinate.X}|{coordinate.Y}) is already in '{farmListName}' (This village is already in the selected farm list.).");
                continue;
            }

            failed++;
            Notify($"{stepPrefix} Failed to save farm ({coordinate.X}|{coordinate.Y}) in '{farmListName}'.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        return new FarmAddBatchResult(
            farmListName,
            requestedCount,
            attempted,
            added,
            alreadyInList,
            failed);
    }

    public async Task<int> EnsureNatarFarmCacheAndReturnToFarmListAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken, forceRefresh);
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        return coordinates.Count;
    }

    public async Task<ManualFarmRunResult> StartManualFarmingFromNatarsAsync(
        string troopType,
        int troopCount,
        int troopVariancePercent,
        bool raidAttack,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        var troopIndex = TroopCatalog.ResolveTroopIndex(troopType.Trim());
        if (troopIndex is null)
        {
            throw new InvalidOperationException($"Could not resolve troop slot for '{troopType}'.");
        }

        await EnsureLoggedInAsync();
        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken);
        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("Could not read any Natar farm coordinates from Natars profile.");
        }

        var sent = 0;
        var skipped = 0;
        var failed = 0;
        var attempted = 0;
        var consecutiveLowTroopSkips = 0;
        var stoppedByNoTroopsAlarm = false;
        for (var i = 0; i < coordinates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinate = coordinates[i];
            if (coordinate.X is null || coordinate.Y is null)
            {
                continue;
            }

            attempted++;
            var stepPrefix = $"[{attempted}/{coordinates.Count}]";
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: attempted > 1);
            var sendResult = await TrySendManualAttackAsync(
                troopType.Trim(),
                troopIndex.Value,
                troopCount,
                troopVariancePercent,
                coordinate.X.Value,
                coordinate.Y.Value,
                raidAttack,
                cancellationToken);

            if (sendResult.Status == ManualAttackSendStatus.Sent)
            {
                consecutiveLowTroopSkips = 0;
                sent++;
                var normalizedVariancePercent = troopVariancePercent switch
                {
                    0 or 5 or 10 or 20 or 50 => troopVariancePercent,
                    _ => 10,
                };
                Notify($"{stepPrefix} Sent {(raidAttack ? "raid" : "normal attack")} to ({coordinate.X}|{coordinate.Y}) with {sendResult.SentTroopCount}/{troopCount}±{normalizedVariancePercent}% {troopType.Trim()} (Available: {FormatLargeCount(sendResult.AvailableTroopCount)}).");
                if (_config.HumanLikeEnabled)
                {
                    var humanDelayMs = Random.Shared.Next(100, 1001);
                    await Task.Delay(humanDelayMs, cancellationToken);
                }
                continue;
            }

            if (sendResult.Status == ManualAttackSendStatus.SkippedLowTroops)
            {
                skipped++;
                consecutiveLowTroopSkips++;
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}). Available {troopType.Trim()}: {FormatLargeCount(sendResult.AvailableTroopCount)}, required minimum: {FormatLargeCount(sendResult.MinimumAcceptedTroopCount)}.");
                if (consecutiveLowTroopSkips >= ManualFarmingMaxConsecutiveLowTroopSkips)
                {
                    Notify($"Stopping manual farming after {consecutiveLowTroopSkips} consecutive low-troop skips.");
                    break;
                }

                continue;
            }

            if (sendResult.Status == ManualAttackSendStatus.StoppedByNoTroopsAlarm)
            {
                stoppedByNoTroopsAlarm = true;
                failed++;
                Notify($"ALARM: manual farming stopped because available {troopType.Trim()} stayed at 0 after {ManualFarmingNoTroopsRetryAttempts} retries.");
                break;
            }

            consecutiveLowTroopSkips = 0;
            failed++;
            Notify($"{stepPrefix} Failed to send {(raidAttack ? "raid" : "normal attack")} to ({coordinate.X}|{coordinate.Y}).");
        }

        return new ManualFarmRunResult(
            coordinates.Count,
            attempted,
            sent,
            skipped,
            failed,
            stoppedByNoTroopsAlarm,
            troopType.Trim(),
            troopCount,
            raidAttack ? "Raid" : "Normal attack");
    }

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageStatusesAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadAllVillageStatusesAsync started");
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the village overview.", cancellationToken);
        await EnsureLoggedInAsync();

        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            return [await ReadCurrentVillageStatusAsync(cancellationToken)];
        }

        var statuses = new List<VillageStatus>();
        foreach (var village in villages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(village.Url))
            {
                await GotoAsync(village.Url, cancellationToken);
            }
            else
            {
                await GotoAsync(_config.VillageOverviewPath, cancellationToken);
            }

            await PauseForManualStepIfVisibleAsync(
                $"Manual verification appeared while switching to village '{village.Name}'.",
                cancellationToken);
            await EnsureLoggedInAsync();
            await ApplyActionDelayAsync(cancellationToken);
            statuses.Add(await ReadCurrentVillageStatusAsync(cancellationToken));
        }

        return statuses;
    }

    public async Task SwitchToVillageAsync(string villageName = "", string? villageUrl = null, CancellationToken cancellationToken = default, bool skipFeatureRefresh = false)
    {
        LogFunctionStarted();
        var activeVillageBeforeSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);

        // If we are already on the requested village, no navigation is needed.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !string.IsNullOrWhiteSpace(activeVillageBeforeSwitch)
            && string.Equals(activeVillageBeforeSwitch, villageName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // karte.php links open the map, they do not switch active village. If a stale picker
        // value passed us a coord URL, ignore it and fall back to a name-based lookup.
        var url = villageUrl;
        if (!string.IsNullOrWhiteSpace(url) && url.Contains("karte.php", StringComparison.OrdinalIgnoreCase))
        {
            url = null;
        }

        // Most reliable path: read the village's switch URL from the in-page sidebar
        // (`<a class="village-name" href="dorf1.php?newdid=X">`). It's present on every page
        // and always carries the correct newdid URL, regardless of server quirks.
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(villageName))
        {
            url = await TryGetVillageHrefFromSidebarAsync(villageName, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            await GotoAsync(url, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(villageName))
        {
            var villages = await ReadVillagesAsync(cancellationToken);
            var match = villages.FirstOrDefault(v =>
                string.Equals(v.Name, villageName, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(v.Url));
            if (match is null)
            {
                throw new InvalidOperationException($"Could not find village '{villageName}' in the village list.");
            }

            await GotoAsync(match.Url!, cancellationToken);
        }
        else
        {
            return;
        }

        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while switching to village '{villageName}'.", cancellationToken);
        await EnsureLoggedInAsync();
        var activeVillageAfterSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        if (!string.Equals(activeVillageBeforeSwitch, activeVillageAfterSwitch, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshCapitalStateForActiveVillageAsync(cancellationToken);
        }

        if (!skipFeatureRefresh)
        {
            // Re-emit account signals so UI refreshes after a village switch (Plus/Gold can be unchanged but UI may not have them yet).
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
        }
    }

    private async Task<string?> TryGetVillageHrefFromSidebarAsync(string villageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var href = await _page.EvaluateAsync<string?>(
                """
                (name) => {
                  const wanted = (name || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  if (!wanted) return null;

                  const candidates = [
                    ...document.querySelectorAll('a.village-name'),
                    ...document.querySelectorAll('#sidebarBoxVillagelist a[href*="newdid"]'),
                    ...document.querySelectorAll('#villageList a[href*="newdid"]'),
                    ...document.querySelectorAll('.villageList a[href*="newdid"]'),
                    ...document.querySelectorAll('a[href*="dorf1.php?newdid="]'),
                    ...document.querySelectorAll('a[href*="dorf2.php?newdid="]')
                  ];

                  const seen = new Set();
                  for (const link of candidates) {
                    const text = (link.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const href = link.getAttribute('href') || '';
                    if (!text || !href || seen.has(link)) continue;
                    seen.add(link);
                    if (text === wanted || text.includes(wanted)) {
                      return href;
                    }
                  }
                  return null;
                }
                """,
                villageName);

            return string.IsNullOrWhiteSpace(href) ? null : ResolveUrl(href);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private async Task<VillageStatus> ReadCurrentVillageStatusAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading village status.", cancellationToken);
        var villages = await ReadVillagesAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var remaining = ResolveShortestQueueDurationSeconds(buildQueue);
        var currency = await ReadCurrencyAsync(cancellationToken);
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        var resources = await ReadResourcesAsync(cancellationToken);
        var capacities = await ReadStorageCapacitiesAsync(cancellationToken);
        var productionByHour = await ReadResourceProductionPerHourAsync(cancellationToken);
        var forecasts = BuildResourceForecasts(resources, capacities, productionByHour);

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: villages,
            Resources: resources,
            ResourceFields: await ReadResourceFieldsAsync(cancellationToken),
            Buildings: await ReadBuildingsAsync(cancellationToken),
            BuildQueue: buildQueue,
            Tribe: await ReadTribeAsync(cancellationToken),
            VillageCount: villages.Count,
            Gold: currency.Gold,
            Silver: currency.Silver,
            IsBuildingInProgress: buildQueue.Count > 0,
            ActiveBuildCount: buildQueue.Count,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? FormatDuration(left) : string.Empty,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc,
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts);
    }

    private async Task GotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{_config.BaseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
        await RetryAsync($"navigate to {pathOrUrl}", async () =>
        {
            var response = await _page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _config.TimeoutMs,
            });
            if (response is not null && response.Headers.TryGetValue("date", out var dateHeader))
            {
                RecordServerTime(dateHeader);
            }
        }, cancellationToken: cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after navigation.", cancellationToken);
        await TryDismissContinuePromptAsync(cancellationToken);
    }

    private bool IsCurrentUrlForPath(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(_page.Url))
        {
            return false;
        }

        try
        {
            if (!Uri.TryCreate(_page.Url, UriKind.Absolute, out var currentUri))
            {
                return false;
            }

            string expectedPath;
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var expectedUri))
                {
                    return false;
                }

                expectedPath = expectedUri.AbsolutePath;
            }
            else
            {
                expectedPath = pathOrUrl.StartsWith('/')
                    ? pathOrUrl
                    : "/" + pathOrUrl;
            }

            return string.Equals(currentUri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureLoggedInAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force
            && _lastEnsureLoggedInSucceeded
            && (now - _lastEnsureLoggedInAt) < EnsureLoggedInMinInterval)
        {
            return;
        }

        Notify("Ensuring logged in");
        var loggedIn = await IsLoggedInAsync();
        _lastEnsureLoggedInAt = now;
        _lastEnsureLoggedInSucceeded = loggedIn;
        if (!loggedIn)
        {
            throw new InvalidOperationException($"Not logged in. Current page state is '{await LoginStateAsync()}'.");
        }
        Notify("Logged in confirmed");
        await TryEmitUiSyncSnapshotAsync(cancellationToken);
    }

    private async Task TryEmitUiSyncSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastUiSyncAt) < UiSyncMinInterval)
        {
            return;
        }

        try
        {
            var currency = await ReadCurrencyAsync(cancellationToken);
            var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
            var villages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new UiSyncSnapshot(
                Gold: currency.Gold,
                Silver: currency.Silver,
                ActiveVillage: activeVillage,
                Villages: villages
                    .Select(v => new UiSyncVillage(v.Name, v.Url, v.IsCapital))
                    .ToList()));
            _lastUiSyncAt = now;
            Notify($"[ui-sync] {payload}");
        }
        catch (Exception ex)
        {
            Notify($"UI sync snapshot failed: {ex.Message}");
        }
    }

    private async Task<bool> IsLoggedInAsync()
    {
        return (await LoginStateAsync()) == "logged_in";
    }

    private async Task<string> LoginStateAsync()
    {
        try
        {
            await TryDismissContinuePromptAsync();

            if (await CaptchaOrManualStepVisibleAsync())
            {
                return "manual_step";
            }

            var currentUrl = _page.Url.ToLowerInvariant();
            if (currentUrl.Contains("login.php", StringComparison.Ordinal))
            {
                Notify("You are logged out");
                return "logged_out";
            }

            foreach (var selector in Selectors.LoggedInIndicators)
            {
                if (await _page.Locator(selector).CountAsync() > 0)
                {
                    Notify("You are logged in");
                    return "logged_in";
                }
            }

            foreach (var selector in Selectors.LoggedOutIndicators)
            {
                if (await _page.Locator(selector).CountAsync() > 0)
                {
                    Notify("You are logged out");
                    return "logged_out";
                }
            }
            Notify("Login state is unknown");
            return "unknown";
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while checking login state. State is unknown.");
            return "unknown";
        }
    }

    private async Task FillFirstAvailableAsync(IEnumerable<string> selectors, string value, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            await RetryAsync($"fill {selector}", async () =>
            {
                await locator.FillAsync(value, new LocatorFillOptions { Timeout = _config.TimeoutMs });
            }, cancellationToken: cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Could not find input field for selectors: {string.Join(", ", selectors)}.");
    }

    private async Task<bool> HasAnySelectorAsync(IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            if (await _page.Locator(selector).CountAsync() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task ClickLoginButtonAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.LoginButton)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            await RetryAsync($"click login selector {selector}", async () =>
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
            }, cancellationToken: cancellationToken);
            Notify("Clicked login button.");
            return;
        }

        throw new InvalidOperationException("Could not find login button.");
    }

    private async Task<bool> TryClickFirstAsync(IEnumerable<string> selectors, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await RetryAsync($"click selector {selector}", async () =>
                {
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after click.", cancellationToken);
                return true;
            }
            catch (PlaywrightException)
            {
                // Try next selector.
            }
            catch (TimeoutException)
            {
                // Try next selector.
            }
        }

        return false;
    }

    private async Task<bool> CaptchaOrManualStepVisibleAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
            """
            (selectors) => {
              const isVisible = (node) => {
                if (!node || !(node instanceof Element)) return false;
                const style = window.getComputedStyle(node);
                if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                const rect = node.getBoundingClientRect();
                if (rect.width <= 0 || rect.height <= 0) return false;
                return true;
              };

              for (const selector of selectors) {
                const nodes = document.querySelectorAll(selector);
                for (const node of nodes) {
                  if (isVisible(node)) return true;
                }
              }

              return false;
            }
            """,
            CaptchaDetectionSelectors);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            // Page is mid-navigation; nothing visible to evaluate. Caller will retry on next tick.
            return false;
        }
    }

    private async Task<bool> TryDismissContinuePromptAsync(CancellationToken cancellationToken = default)
    {
        if (_page.IsClosed)
        {
            return false;
        }

        var clickTimeoutMs = Math.Min(Math.Max(_config.TimeoutMs / 4, 500), 2500);
        var hadMatch = false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = await FindContinuePromptLocatorAsync(clickTimeoutMs);
            if (candidate is null)
            {
                return false;
            }

            hadMatch = true;

            try
            {
                if (!await IsLocatorVisibleAsync(candidate, clickTimeoutMs))
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(120, cancellationToken);
                        continue;
                    }

                    break;
                }

                await candidate.ClickAsync(new LocatorClickOptions { Timeout = clickTimeoutMs });
                Notify("Detected update popup. Clicked 'Continue' automatically.");
                await Task.Delay(220, cancellationToken);
                return true;
            }
            catch (PlaywrightException ex)
            {
                if (attempt < 2)
                {
                    Notify($"Found 'Continue' prompt but click failed on attempt {attempt}/2. Retrying...");
                    await Task.Delay(120, cancellationToken);
                    continue;
                }

                Notify($"Found 'Continue' prompt but could not click it: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                if (attempt < 2)
                {
                    Notify($"Found 'Continue' prompt but click timed out on attempt {attempt}/2. Retrying...");
                    await Task.Delay(120, cancellationToken);
                    continue;
                }

                Notify($"Found 'Continue' prompt but click timed out: {ex.Message}");
            }
        }

        if (hadMatch)
        {
            Notify("Found 'Continue' prompt but it was not clickable. Continuing with normal flow.");
        }

        return false;
    }

    private async Task<ILocator?> FindContinuePromptLocatorAsync(int timeoutMs)
    {
        try
        {
            var directContinueLink = _page.Locator(Selectors.ContinueAfterUpdateLink);
            if (await directContinueLink.CountAsync() > 0 && await IsLocatorVisibleAsync(directContinueLink.First, timeoutMs))
            {
                return directContinueLink.First;
            }

            var textSelectors = new[]
            {
                "button",
                "a",
                "[role='button']",
            };

            foreach (var selector in textSelectors)
            {
                var candidates = _page.Locator(selector);
                var count = Math.Min(await candidates.CountAsync(), 20);
                for (var index = 0; index < count; index++)
                {
                    var candidate = candidates.Nth(index);
                    string? text;
                    try
                    {
                        text = (await candidate.InnerTextAsync())?.Trim();
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (text.IndexOf("Continue", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (await IsLocatorVisibleAsync(candidate, timeoutMs))
                    {
                        return candidate;
                    }
                }
            }

            var inputSelectors = new[]
            {
                "input[type='button']",
                "input[type='submit']",
            };

            foreach (var selector in inputSelectors)
            {
                var candidates = _page.Locator(selector);
                var count = Math.Min(await candidates.CountAsync(), 6);
                for (var index = 0; index < count; index++)
                {
                    var candidate = candidates.Nth(index);
                    string? value;
                    try
                    {
                        value = await candidate.GetAttributeAsync("value");
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (value.IndexOf("Continue", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (await IsLocatorVisibleAsync(candidate, timeoutMs))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private static async Task<bool> IsLocatorVisibleAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            _ = timeoutMs;
            return await locator.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> WaitUntilLoggedInAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _config.ManualLoginTimeoutSeconds));
        var manualMessageShown = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await TryDismissContinuePromptAsync(cancellationToken);

                if (await IsLoggedInAsync())
                {
                    return true;
                }

                if (await CaptchaOrManualStepVisibleAsync() && !manualMessageShown)
                {
                    await CaptureManualVerificationScreenshotAsync("login-wait", cancellationToken);
                    Notify("Captcha/manual step detected. Solve it in the browser window, then wait here.");
                    if (!_browserVisible)
                    {
                        throw new ManualVerificationRequiredException(
                            "Captcha/manual verification appeared while running headless.");
                    }

                    manualMessageShown = true;
                }

                await Task.Delay(500, cancellationToken);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify("Page navigated while checking login state. Retrying...");
                await Task.Delay(220, cancellationToken);
            }
        }

        if (!_interactive)
        {
            throw new InvalidOperationException("Login was not confirmed before timeout.");
        }

        Notify("Login is not confirmed yet. Finish login/captcha in the browser if needed.");
        Console.WriteLine("Press Enter after the village overview is visible...");
        Console.ReadLine();
        await EnsureLoggedInAsync();
        return true;
    }

    private async Task PauseForManualStepIfVisibleAsync(string message, CancellationToken cancellationToken)
    {
        if (!await CaptchaOrManualStepVisibleAsync())
        {
            return;
        }

        var screenshotPath = await CaptureManualVerificationScreenshotAsync("manual-verification", cancellationToken);
        if (await TrySolveCaptchaAutomaticallyAsync("manual-verification", screenshotPath, cancellationToken))
        {
            Notify("Captcha cleared automatically. Continuing.");
            return;
        }

        Notify($"{message} Solve it in the browser window. The bot is paused.");
        if (!_browserVisible)
        {
            throw new ManualVerificationRequiredException(
                "Captcha/manual verification appeared while running headless.");
        }

        if (_interactive)
        {
            Console.WriteLine("Press Enter after the manual step is solved...");
            Console.ReadLine();
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _config.ManualLoginTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await CaptchaOrManualStepVisibleAsync())
            {
                Notify("Manual verification cleared. Continuing.");
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException(
            "Manual verification was still visible after timeout. Solve it and run again.");
    }

    private async Task<string?> CaptureManualVerificationScreenshotAsync(string label, CancellationToken cancellationToken, bool force = false)
    {
        if (_page.IsClosed)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastManualVerificationScreenshotAt) < TimeSpan.FromSeconds(10))
        {
            return null;
        }

        _lastManualVerificationScreenshotAt = now;
        var safeLabel = SafePathSegment(label);
        var stamp = now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var captchaRoot = Path.Combine(
            _projectRoot,
            "logs",
            "captchas");
        Directory.CreateDirectory(captchaRoot);

        var screenshotPath = Path.Combine(captchaRoot, $"{stamp}-{safeLabel}.png");

        try
        {
            var clipRegion = await WaitForCaptchaVisualAsync(cancellationToken);
            if (clipRegion is not null)
            {
                await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    Clip = new Clip
                    {
                        X = (float)clipRegion.X,
                        Y = (float)clipRegion.Y,
                        Width = (float)clipRegion.Width,
                        Height = (float)clipRegion.Height,
                    },
                });
            }
            else
            {
                await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = true,
                });
            }

            Notify($"Captured captcha screenshot: '{screenshotPath}'.");
            return screenshotPath;
        }
        catch (Exception ex)
        {
            Notify($"Could not capture captcha screenshot for '{label}': {ex.Message}");
        }

        return null;
    }

    private async Task<CaptchaClipRegion?> WaitForCaptchaVisualAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        CaptchaClipRegion? previous = null;
        var stableMatches = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryResolveCaptchaClipRegionAsync(cancellationToken);
            if (current is not null)
            {
                if (previous is not null && AreCaptchaRegionsSimilar(previous, current))
                {
                    stableMatches++;
                    if (stableMatches >= 2)
                    {
                        await Task.Delay(300, cancellationToken);
                        return current;
                    }
                }
                else
                {
                    stableMatches = 0;
                }

                previous = current;
            }

            await Task.Delay(150, cancellationToken);
        }

        return previous;
    }

    private async Task<CaptchaClipRegion?> TryResolveCaptchaClipRegionAsync(CancellationToken cancellationToken)
    {
        var locator = await TryFindVisibleCaptchaLocatorAsync();
        if (locator is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await locator.ScrollIntoViewIfNeededAsync();

        try
        {
            return await locator.EvaluateAsync<CaptchaClipRegion?>(
                """
                node => {
                  const isVisible = element => {
                    if (!element || !(element instanceof Element)) return false;
                    const style = window.getComputedStyle(element);
                    if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                    const rect = element.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };

                  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
                  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
                  const viewportArea = Math.max(1, viewportWidth * viewportHeight);
                  const nodeRect = node.getBoundingClientRect();
                  if (!isVisible(nodeRect) && !isVisible(node)) return null;

                  let bestRect = nodeRect;
                  let bestArea = Math.max(1, nodeRect.width * nodeRect.height);
                  let current = node.parentElement;
                  let depth = 0;
                  const extractExpression = text => {
                    if (!text) return null;
                    const normalized = text.replace(/\s+/g, ' ').trim();
                    const match = normalized.match(/(\d+)\s*([+\-])\s*(\d+)\s*=\s*\?/);
                    return match ? `${match[1]}${match[2]}${match[3]}` : null;
                  };

                  while (current && depth < 6) {
                    if (isVisible(current)) {
                      const rect = current.getBoundingClientRect();
                      const area = rect.width * rect.height;
                      const notTooLarge = area <= viewportArea * 0.75;
                      const largerThanNode = rect.width >= nodeRect.width && rect.height >= nodeRect.height;
                      const containsExpression = !!extractExpression(current.textContent || '');
                      const containsCaptchaUi =
                        containsExpression
                        || (current.textContent || '').toLowerCase().includes('security check')
                        || (current.textContent || '').toLowerCase().includes('calculate')
                        || (current.textContent || '').toLowerCase().includes('verify');
                      if (notTooLarge && largerThanNode && (containsCaptchaUi || area >= bestArea * 1.1)) {
                        bestRect = rect;
                        bestArea = area;
                      }
                    }

                    current = current.parentElement;
                    depth += 1;
                  }

                  if (bestRect.width < 140 || bestRect.height < 40) {
                    return null;
                  }

                  const padX = Math.max(24, Math.min(80, bestRect.width * 0.12));
                  const padY = Math.max(24, Math.min(80, bestRect.height * 0.18));
                  const x = Math.max(0, bestRect.left - padX);
                  const y = Math.max(0, bestRect.top - padY);
                  const right = Math.min(viewportWidth, bestRect.right + padX);
                  const bottom = Math.min(viewportHeight, bestRect.bottom + padY);
                  const width = Math.max(1, right - x);
                  const height = Math.max(1, bottom - y);

                  return { x, y, width, height };
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private async Task<ILocator?> TryFindVisibleCaptchaLocatorAsync()
    {
        foreach (var selector in CaptchaDetectionSelectors)
        {
            var candidates = _page.Locator(selector);
            int count;
            try
            {
                count = await candidates.CountAsync();
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                return null;
            }
            catch (PlaywrightException)
            {
                continue;
            }

            var limit = Math.Min(count, 8);
            for (var index = 0; index < limit; index++)
            {
                var candidate = candidates.Nth(index);
                try
                {
                    if (await candidate.IsVisibleAsync())
                    {
                        return candidate;
                    }
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    return null;
                }
                catch (PlaywrightException)
                {
                    // Try next candidate.
                }
            }
        }

        return null;
    }

    private static bool AreCaptchaRegionsSimilar(CaptchaClipRegion previous, CaptchaClipRegion current)
    {
        return Math.Abs(previous.X - current.X) <= 6
            && Math.Abs(previous.Y - current.Y) <= 6
            && Math.Abs(previous.Width - current.Width) <= 18
            && Math.Abs(previous.Height - current.Height) <= 18;
    }

    private async Task ApplyActionDelayAsync(CancellationToken cancellationToken)
    {
        if (!_config.HumanLikeEnabled)
        {
            return;
        }

        var ranges = new Dictionary<string, (double low, double high)>(StringComparer.OrdinalIgnoreCase)
        {
            ["slow"] = (2.5, 5.0),
            ["medium"] = (1.0, 2.5),
            ["fast"] = (0.3, 1.0),
        };

        var speed = _config.HumanLikeSpeed ?? "medium";
        var selectedRange = ranges.TryGetValue(speed, out var range) ? range : ranges["medium"];
        var delayMs = Random.Shared.Next((int)(selectedRange.low * 1000), (int)(selectedRange.high * 1000));
        await Task.Delay(delayMs, cancellationToken);
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesAsync(CancellationToken cancellationToken)
    {
        Notify("[ReadVillagesAsync] started");
        if (_cachedVillages is { Count: > 0 } cached
            && DateTimeOffset.UtcNow - _cachedVillagesAt < VillagesCacheTtl)
        {
            return cached;
        }

        var villages = await ReadVillagesFromServerAsync(cancellationToken);
        if (villages.Count > 0)
        {
            _cachedVillages = villages.ToList();
            _cachedVillagesAt = DateTimeOffset.UtcNow;
        }
        Notify("[ReadVillagesAsync] finished");
        return villages;
    }

    // Like ReadVillagesAsync but never navigates to spieler.php just to refresh the list.
    // Order: fresh cache -> stale cache -> sidebar of current page -> server (last resort).
    // Used by lightweight refresh paths (e.g. post-upgrade) where the page navigation would
    // appear to the user as an unnecessary refresh.
    private async Task<IReadOnlyList<Village>> ReadVillagesPreferCacheAsync(CancellationToken cancellationToken)
    {
        if (_cachedVillages is { Count: > 0 } cached
            && DateTimeOffset.UtcNow - _cachedVillagesAt < VillagesCacheTtl)
        {
            return cached;
        }

        try
        {
            var sidebar = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            if (sidebar.Count > 0)
            {
                if (_cachedVillages is { Count: > 0 } prior)
                {
                    var merged = sidebar
                        .Select(v =>
                        {
                            var match = prior.FirstOrDefault(p => string.Equals(p.Name, v.Name, StringComparison.Ordinal));
                            if (match is null)
                            {
                                return v;
                            }
                            return v with
                            {
                                IsCapital = v.IsCapital ?? match.IsCapital,
                                CoordX = match.CoordX,
                                CoordY = match.CoordY,
                                Population = match.Population,
                                CropFields = match.CropFields,
                            };
                        })
                        .ToList();
                    _cachedVillages = merged;
                    _cachedVillagesAt = DateTimeOffset.UtcNow;
                    return merged;
                }

                _cachedVillages = sidebar.ToList();
                _cachedVillagesAt = DateTimeOffset.UtcNow;
                return sidebar;
            }
        }
        catch (Exception ex)
        {
            Notify($"[ReadVillagesPreferCacheAsync] sidebar read failed, falling back: {ex.Message}");
        }

        if (_cachedVillages is { Count: > 0 } stale)
        {
            return stale;
        }

        return await ReadVillagesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages from current page.", cancellationToken);

        var raw = await _page.EvaluateAsync<SidebarVillageJs[]>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const rows = [];
              const seen = new Set();
              const selectors = [
                '#sidebarBoxVillagelist a[href*="newdid="]',
                '#villageList a[href*="newdid="]',
                '.villageList a[href*="newdid="]',
                'a.village-name[href*="newdid="]'
              ];

              for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                  const name = clean(node.textContent || node.getAttribute('title') || '');
                  const url = clean(node.getAttribute('href') || '');
                  if (!name || !url) continue;
                  const key = `${name}|${url}`;
                  if (seen.has(key)) continue;
                  seen.add(key);
                  const container = node.closest('li, .active, .listEntry, .village') || node.parentElement || node;
                  const classText = clean(`${node.className || ''} ${container.className || ''}`).toLowerCase();
                  rows.push({
                    name,
                    url,
                    isCapital: classText.includes('capital') ? true : null
                  });
                }

                if (rows.length > 0) {
                  return rows;
                }
              }

              return rows;
            }
            """);

        return raw
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item =>
            {
                var (cachedX, cachedY) = TryGetCachedVillageCoords(item.Name!);
                return new Village(
                    Name: item.Name!,
                    Url: item.Url,
                    IsCapital: item.IsCapital ?? TryGetCachedCapitalState(item.Name!),
                    CoordX: cachedX,
                    CoordY: cachedY);
            })
            .ToList();
    }

    private void InvalidateVillagesCache() => _cachedVillagesAt = DateTimeOffset.MinValue;

    private void UpdateCachedVillages(IReadOnlyList<Village> villages)
    {
        if (villages.Count == 0)
        {
            return;
        }

        _cachedVillages = villages.ToList();
        _cachedVillagesAt = DateTimeOffset.UtcNow;
    }

    private async Task<IReadOnlyList<Village>> ReadVillagesFromServerAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages.", cancellationToken);
        var previousUrl = _page.Url;
        try
        {
            await GotoAsync(Paths.PlayerProfile, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages on spieler.php.", cancellationToken);
            await EnsureLoggedInAsync();

            var raw = await _page.EvaluateAsync<PlayerProfileVillageRowJs[]>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const parseIntFromText = (value) => {
                    const match = clean(value).match(/(\d[\d\s.]*)/);
                    if (!match) return null;
                    const digits = match[1].replace(/[^\d]/g, '');
                    if (!digits) return null;
                    const parsed = Number.parseInt(digits, 10);
                    return Number.isFinite(parsed) ? parsed : null;
                  };
                  const parseCoords = (textOrHref) => {
                    const source = textOrHref || '';
                    const xQuery = source.match(/[?&]x=(-?\d+)/i);
                    const yQuery = source.match(/[?&]y=(-?\d+)/i);
                    if (xQuery && yQuery) {
                      return { x: Number.parseInt(xQuery[1], 10), y: Number.parseInt(yQuery[1], 10) };
                    }

                    const pair = source.match(/(-?\d+)\s*[|,]\s*(-?\d+)/);
                    if (!pair) return { x: null, y: null };
                    return {
                      x: Number.parseInt(pair[1], 10),
                      y: Number.parseInt(pair[2], 10)
                    };
                  };

                  const rows = [];
                  const seen = new Set();
                  for (const row of document.querySelectorAll('table tr')) {
                    const rowText = clean(row.textContent || '');
                    if (!rowText) continue;

                    // Prefer village-switch URLs (newdid / dorf) over coord links (karte.php).
                    // Some servers render the village name as a karte link, which would make the
                    // bot navigate to the map instead of switching villages.
                    const nameAnchor =
                      row.querySelector('td.name a[href*="newdid"], td.village a[href*="newdid"], td:nth-child(1) a[href*="newdid"]')
                      || row.querySelector('td.name a[href*="dorf"], td.village a[href*="dorf"], td:nth-child(1) a[href*="dorf"]')
                      || row.querySelector('a[href*="newdid"]')
                      || row.querySelector('a[href*="dorf1.php"], a[href*="dorf2.php"]')
                      || row.querySelector('td.name a[href]:not([href*="karte"]), td.village a[href]:not([href*="karte"]), td:nth-child(1) a[href]:not([href*="karte"])')
                      || row.querySelector('td.name a[href], td.village a[href], td:nth-child(1) a[href]');
                    const name = clean(nameAnchor?.textContent || row.querySelector('td.name, td.village')?.textContent || '');
                    if (!name) continue;

                    const profileLikeRow = !!row.querySelector('td.coords, a[href*="karte.php"], span.mainVillage');
                    if (!profileLikeRow) continue;

                    const villageHref = nameAnchor?.getAttribute('href') || '';
                    const coordAnchor = row.querySelector('td.coords a[href*="karte.php"], a[href*="karte.php"]');
                    const coordHref = coordAnchor?.getAttribute('href') || '';
                    const coordText = clean(coordAnchor?.textContent || row.querySelector('td.coords')?.textContent || '');
                    const coord = parseCoords(coordHref || coordText || rowText);

                    const popText = clean(
                      row.querySelector('td.inhabitants, td.population, td.pop')?.textContent
                      || row.querySelector('td:nth-child(2)')?.textContent
                      || '');
                    let population = parseIntFromText(popText);
                    if (population === null) {
                      const compactNumbers = rowText.match(/\b\d{2,6}\b/g) || [];
                      if (compactNumbers.length > 0) {
                        population = Number.parseInt(compactNumbers[compactNumbers.length - 1], 10);
                      }
                    }

                    const cropMatch = rowText.match(/\b(\d{1,2})\s*c\b/i) || name.match(/\b(\d{1,2})\s*c\b/i);
                    const cropFields = cropMatch ? Number.parseInt(cropMatch[1], 10) : null;

                    const isCapital = !!row.querySelector('span.mainVillage, td.isCapital, .capital');
                    const key = `${name}|${coord.x ?? ''}|${coord.y ?? ''}`;
                    if (seen.has(key)) continue;
                    seen.add(key);

                    rows.push({
                      name,
                      url: villageHref || '',
                      isCapital,
                      x: Number.isFinite(coord.x) ? coord.x : null,
                      y: Number.isFinite(coord.y) ? coord.y : null,
                      population: Number.isFinite(population) ? population : null,
                      cropFields: Number.isFinite(cropFields) ? cropFields : null
                    });
                  }

                  return rows;
                }
                """);

            var rawList = (raw ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList();
            // If the spieler scan identified at least one capital village, trust the per-row data
            // verbatim — there is exactly one capital, and an OR with stale cache could keep an
            // old village marked as capital after the capital is moved.
            var trustScanCapital = rawList.Any(v => v.IsCapital);

            var villages = rawList
                .Select(v =>
                {
                    var cachedCapital = TryGetCachedCapitalState(v.Name!);
                    var (cachedX, cachedY) = TryGetCachedVillageCoords(v.Name!);
                    var resolvedCapital = trustScanCapital ? v.IsCapital : (v.IsCapital || cachedCapital == true);
                    var resolvedX = v.X ?? cachedX;
                    var resolvedY = v.Y ?? cachedY;
                    SaveCachedVillageState(v.Name!, resolvedCapital, resolvedX, resolvedY);
                    return new Village(
                        Name: v.Name!,
                        Url: ResolveUrl(v.Url ?? string.Empty),
                        IsCapital: resolvedCapital,
                        CoordX: resolvedX,
                        CoordY: resolvedY,
                        Population: v.Population,
                        CropFields: v.CropFields);
                })
                .OrderByDescending(v => v.IsCapital == true)
                .ThenByDescending(v => v.Population ?? -1)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (villages.Count > 0)
            {
                return villages;
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousUrl))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }

        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '#sidebarBoxVillagelist a[href*="newdid"]',
                '#villageList a[href*="newdid"]',
                '.villageList a[href*="newdid"]',
                'a[href*="newdid"]'
              ];
              const seen = new Set();
              const villages = [];

              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const name = (element.textContent || '').replace(/\s+/g, ' ').trim();
                  const href = element.getAttribute('href');
                  const key = `${name}|${href}`;
                  if (!name || seen.has(key)) continue;
                  seen.add(key);
                  villages.push([name, href || '']);
                }
                if (villages.length) return JSON.stringify(villages);
              }

              const heading = document.querySelector('h1, .titleInHeader, #content h2');
              const fallbackName = heading ? heading.textContent.replace(/\s+/g, ' ').trim() : '';
              return JSON.stringify(fallbackName ? [[fallbackName, '']] : []);
            }
            """);

        var rawFallback = string.IsNullOrWhiteSpace(rawJson)
            ? new List<List<string>>()
            : JsonSerializer.Deserialize<List<List<string>>>(rawJson) ?? new List<List<string>>();

        rawFallback ??= [];
        return rawFallback
            .Where(v => v.Count > 0 && !string.IsNullOrWhiteSpace(v[0]))
            .Select(v =>
            {
                var name = v[0];
                var (cx, cy) = TryGetCachedVillageCoords(name);
                return new Village(
                    Name: name,
                    Url: ResolveUrl(v.Count > 1 ? v[1] : string.Empty),
                    IsCapital: TryGetCachedCapitalState(name),
                    CoordX: cx,
                    CoordY: cy,
                    Population: null,
                    CropFields: null);
            })
            .OrderByDescending(v => v.IsCapital == true)
            .ThenByDescending(v => v.Population ?? -1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(int? Warehouse, int? Granary)> ReadStorageCapacitiesAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading storage capacity.", cancellationToken);
        var raw = await _page.EvaluateAsync<Dictionary<string, int?>>(
            """
            () => {
              const parseNumber = (value) => {
                const text = (value || '').replace(/\s+/g, ' ').trim();
                if (!text) return null;
                const match = text.match(/(\d[\d\s.,']*)/);
                if (!match) return null;
                const digits = match[1].replace(/[^\d]/g, '');
                if (!digits) return null;
                const parsed = Number(digits);
                return Number.isFinite(parsed) ? parsed : null;
              };

              const readFirst = (selectors) => {
                for (const selector of selectors) {
                  for (const node of document.querySelectorAll(selector)) {
                    const value =
                      parseNumber(node.getAttribute('data-value'))
                      ?? parseNumber(node.getAttribute('data-max'))
                      ?? parseNumber(node.getAttribute('data-capacity'))
                      ?? parseNumber(node.getAttribute('title'))
                      ?? parseNumber(node.getAttribute('aria-label'))
                      ?? parseNumber(node.textContent || '');
                    if (value !== null) return value;
                  }
                }
                return null;
              };

              return {
                warehouse: readFirst([
                  '#stockBarWarehouse .value',
                  '#stockBarWarehouse',
                  '#warehouse .value',
                  '#warehouse',
                  '[id*="warehouse" i][data-max]',
                  '[class*="warehouse" i]'
                ]),
                granary: readFirst([
                  '#stockBarGranary .value',
                  '#stockBarGranary',
                  '#stockBarSilo .value',
                  '#stockBarSilo',
                  '#granary .value',
                  '#granary',
                  '#silo .value',
                  '#silo',
                  '[id*="granary" i][data-max]',
                  '[id*="silo" i][data-max]',
                  '[class*="granary" i]',
                  '[class*="silo" i]'
                ])
              };
            }
            """);

        if (raw is null)
        {
            return (null, null);
        }

        raw.TryGetValue("warehouse", out var warehouse);
        raw.TryGetValue("granary", out var granary);
        return (warehouse, granary);
    }

    private async Task<(int? Gold, int? Silver)> ReadCurrencyAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading gold/silver.", cancellationToken);
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const hasGold = !!document.querySelector('#ajaxReplaceableGoldAmount_2, [id^="ajaxReplaceableGoldAmount_"], .ajaxReplaceableGoldAmount, #gold');
                  const hasSilver = !!document.querySelector('#silver, #silverValue, [id*="silver" i], [class*="silver" i], font[color="#B3B3B3"], font[color="#b3b3b3"]');
                  return hasGold || hasSilver;
                }
                """,
                new PageWaitForFunctionOptions { Timeout = 2500 });
        }
        catch
        {
            // Continue with fallback polling.
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await _page.EvaluateAsync<Dictionary<string, int?>>(
                """
                () => {
                  const parseNumber = (value) => {
                    const text = (value || '').replace(/\s+/g, ' ').trim();
                    if (!text) return null;
                    const match = text.match(/(\d[\d\s.,']*)/);
                    if (!match) return null;
                    const digits = match[1].replace(/[^\d]/g, '');
                    if (!digits) return null;
                    const parsed = Number(digits);
                    return Number.isFinite(parsed) ? parsed : null;
                  };

                  const readFirstNumber = (selectors) => {
                    for (const selector of selectors) {
                      for (const node of document.querySelectorAll(selector)) {
                        const value =
                          parseNumber(node.getAttribute('data-value'))
                          ?? parseNumber(node.getAttribute('data-amount'))
                          ?? parseNumber(node.textContent || '')
                          ?? parseNumber(node.getAttribute('title') || '')
                          ?? parseNumber(node.getAttribute('aria-label') || '');
                        if (value !== null) return value;
                      }
                    }

                    return null;
                  };

                  const readFromHtmlPattern = (regex) => {
                    const html = document.documentElement?.innerHTML || '';
                    const match = html.match(regex);
                    if (!match || match.length < 2) return null;
                    return parseNumber(match[1] || '');
                  };

                  const readFromLabel = (labels) => {
                    const lines = (document.body?.innerText || '').split(/\n+/).map(line => line.trim()).filter(Boolean);
                    for (const line of lines) {
                      for (const label of labels) {
                        if (!new RegExp(`\\b${label}\\b`, 'i').test(line)) continue;
                        const value = parseNumber(line);
                        if (value !== null) return value;
                      }
                    }
                    return null;
                  };

                  const gold =
                    readFirstNumber([
                      '#ajaxReplaceableGoldAmount_2',
                      '[id^="ajaxReplaceableGoldAmount_"]',
                      '.ajaxReplaceableGoldAmount',
                      '#gold',
                      '#gold .value',
                      '[id*="gold" i]',
                      '[class*="gold" i]'
                    ])
                    ?? readFromHtmlPattern(/id=["']ajaxReplaceableGoldAmount_[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromLabel(['gold', 'guld', 'premium']);

                  const silver =
                    readFirstNumber([
                      '#silver',
                      '#silverValue',
                      '[id^="ajaxReplaceableSilverAmount_"]',
                      '.ajaxReplaceableSilverAmount',
                      '#sidebarBoxActiveVillage #silver',
                      '#sidebarBoxActiveVillage .silver',
                      "font[color='#B3B3B3']",
                      "font[color='#b3b3b3']"
                    ])
                    ?? readFromHtmlPattern(/<font[^>]*color=["']#b3b3b3["'][^>]*>([^<]+)/i)
                    ?? readFromLabel(['silver', 'silber']);

                  return { gold, silver };
                }
                """);

            if (raw is not null)
            {
                raw.TryGetValue("gold", out var gold);
                raw.TryGetValue("silver", out var silver);
                if (gold is not null || silver is not null)
                {
                    return (gold, silver);
                }
            }

            if (attempt < 4)
            {
                await Task.Delay(220, cancellationToken);
            }
        }

        var locatorGold = await ReadNumberFromSelectorsAsync(
            [
                "#ajaxReplaceableGoldAmount_2",
                "[id^='ajaxReplaceableGoldAmount_']",
                ".ajaxReplaceableGoldAmount",
                "#gold",
                "[id*='gold' i]"
            ],
            cancellationToken);
        var locatorSilver = await ReadNumberFromSelectorsAsync(
            [
                "#silver",
                "#silverValue",
                "[id^='ajaxReplaceableSilverAmount_']",
                ".ajaxReplaceableSilverAmount",
                "#sidebarBoxActiveVillage #silver",
                "#sidebarBoxActiveVillage .silver",
                "font[color='#B3B3B3']",
                "font[color='#b3b3b3']"
            ],
            cancellationToken);
        if (locatorGold is not null || locatorSilver is not null)
        {
            return (locatorGold, locatorSilver);
        }

        Notify("Could not detect gold/silver values on this page. Returning '-'.");
        return (null, null);
    }

    private async Task<int?> ReadNumberFromSelectorsAsync(IEnumerable<string> selectors, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var locator = _page.Locator(selector).First;
                if (await locator.CountAsync() == 0)
                {
                    continue;
                }

                var text = await locator.InnerTextAsync();
                var parsed = ParseNumericTextToInt(text);
                if (parsed is not null)
                {
                    return parsed;
                }

                var title = await locator.GetAttributeAsync("title");
                parsed = ParseNumericTextToInt(title);
                if (parsed is not null)
                {
                    return parsed;
                }

                var aria = await locator.GetAttributeAsync("aria-label");
                parsed = ParseNumericTextToInt(aria);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
                // Try next selector.
            }
        }

        return null;
    }

    internal static int? ParseNumericTextToInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        var match = Regex.Match(normalized, @"(\d[\d\s\.,']*)");
        if (!match.Success)
        {
            return null;
        }

        var digits = Regex.Replace(match.Groups[1].Value, @"\D", string.Empty);
        if (digits.Length == 0)
        {
            return null;
        }

        return int.TryParse(digits, out var parsed) ? parsed : null;
    }

    private async Task<string> ReadActiveVillageNameAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the active village.", cancellationToken);
        var value = await _page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '#villageNameField',
                '#villageNameField.boxTitle',
                '.villageList .active',
                '#villageList .active',
                '#sidebarBoxVillagelist .active',
                '.villageNameField',
                'h1',
                '.titleInHeader'
              ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                const text = element ? (element.textContent || '').replace(/\s+/g, ' ').trim() : '';
                if (text) return text;
              }

              return 'Unknown village';
            }
            """);
        return string.IsNullOrWhiteSpace(value) ? "Unknown village" : value;
    }

    private async Task<string> ReadTribeAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading tribe.", cancellationToken);
        var value = await _page.EvaluateAsync<string>(
            """
            () => {
              const tribeNames = {
                1: 'Romans',
                2: 'Teutons',
                3: 'Gauls',
                4: 'Nature',
                5: 'Natars',
                6: 'Egyptians',
                7: 'Huns',
                8: 'Spartans'
              };
              const altNorm = (raw) => {
                const t = (raw || '').toLowerCase();
                if (t.startsWith('roman')) return 'Romans';
                if (t.startsWith('teuton')) return 'Teutons';
                if (t.startsWith('gaul')) return 'Gauls';
                if (t.startsWith('egypt')) return 'Egyptians';
                if (t.startsWith('hun')) return 'Huns';
                if (t.startsWith('spartan')) return 'Spartans';
                return null;
              };
              const srcNorm = (raw) => {
                const m = (raw || '').match(/(roman|teuton|gaul|egypt|hun|spartan)/i);
                return m ? altNorm(m[1]) : null;
              };

              // Primary: tribe icon img (works directly from dorf1/dorf2).
              for (const img of document.querySelectorAll('img.nationBig, img[src*="/tribes/"], img[src*="nation"], img[alt]')) {
                const fromAlt = altNorm(img.getAttribute('alt'));
                if (fromAlt) return fromAlt;
                const fromSrc = srcNorm(img.getAttribute('src'));
                if (fromSrc) return fromSrc;
              }

              const selectors = [
                '[class*="tribe" i]',
                '[id*="tribe" i]',
                '.playerInfo',
                '#sidebarBoxActiveVillage',
                '#sidebarBoxVillagelist',
                'body'
              ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                if (!element) continue;
                const text = `${element.className || ''} ${element.getAttribute('title') || ''} ${element.textContent || ''}`.toLowerCase();
                if (text.includes('roman')) return 'Romans';
                if (text.includes('teuton')) return 'Teutons';
                if (text.includes('gaul')) return 'Gauls';
                if (text.includes('egypt')) return 'Egyptians';
                if (text.includes('hun')) return 'Huns';
                if (text.includes('spartan')) return 'Spartans';

                const tribeMatch = text.match(/tribe[^0-9]*(\d+)/i) || text.match(/tribe(\d+)/i);
                if (tribeMatch && tribeNames[Number(tribeMatch[1])]) return tribeNames[Number(tribeMatch[1])];
              }

              return 'Unknown';
            }
            """);

        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private async Task<bool> ReadGoldClubEnabledAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading gold club status.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              if (document.querySelector('#buttonBuild')) return true;
              // Fallback: Gold Club-knappen kan signaleras via klass i sidebaren utan #buttonBuild på vissa varianter.
              const sidebar = document.querySelector('#sidebarBoxVillagelist');
              return /buildOff|buildOn|builder=On/.test(sidebar?.innerHTML || '');
            }
            """);
    }

    private async Task EnsureRallyPointAndOpenFarmListPageAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.FarmListPage, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening farmlists.", cancellationToken);
        await EnsureLoggedInAsync();
        if (await IsFarmListPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.FarmListFastUp, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening rally point slot.", cancellationToken);
        await EnsureLoggedInAsync();

        try
        {
            var constructResult = await ConstructBuildingAsync(39, 16, "Rally Point", cancellationToken);
            Notify($"Rally Point ensure result: {constructResult}");
        }
        catch (Exception ex)
        {
            Notify($"Could not auto-construct Rally Point on slot 39: {ex.Message}");
        }

        await GotoAsync(Paths.FarmListPage, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening farmlists.", cancellationToken);
        await EnsureLoggedInAsync();

        if (!await IsFarmListPageAsync(cancellationToken))
        {
            throw new InvalidOperationException("Could not open farm list page at build.php?id=39&t=99. Farmlists may be unavailable on this account/server.");
        }
    }

    private async Task EnsureRallyPointAndOpenSendTroopsPageAsync(CancellationToken cancellationToken, bool allowReuseCurrentPage)
    {
        if (allowReuseCurrentPage && await IsSendTroopsPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.RallyPointSendTroops, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening send troops.", cancellationToken);
        await EnsureLoggedInAsync();
        if (await IsSendTroopsPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.FarmListFastUp, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening rally point slot.", cancellationToken);
        await EnsureLoggedInAsync();

        await GotoAsync(Paths.RallyPointSendTroops, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reopening send troops.", cancellationToken);
        await EnsureLoggedInAsync();
        if (await IsSendTroopsPageAsync(cancellationToken))
        {
            return;
        }

        var tabOpened = await TryOpenSendTroopsTabAsync(cancellationToken);
        if (tabOpened)
        {
            await Task.Delay(250, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after opening send troops tab.", cancellationToken);
            await EnsureLoggedInAsync();
        }

        if (!await IsSendTroopsPageAsync(cancellationToken))
        {
            throw new InvalidOperationException("Rally Point does not appear to be constructed yet. Build Rally Point before starting manual farming.");
        }
    }

    private async Task<bool> IsFarmListPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while checking farm list page.", cancellationToken);
        var isFarmListPage = await _page.EvaluateAsync<bool>(
            """
            () => {
              if (document.querySelector('span[id^="timerTop"]')) return true;
              if (document.querySelector('.farmList, .farmlist, [class*="farm" i][class*="list" i]')) return true;

              const body = (document.body?.innerText || '').toLowerCase();
              return body.includes('start raid') || body.includes('farm list') || body.includes('farmlist');
            }
            """);
        return isFarmListPage;
    }

    private async Task<bool> IsSendTroopsPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while checking send troops page.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const hasCoords = !!document.querySelector('input[name="x"], input[name="y"], input[name*="xCoord" i], input[name*="yCoord" i], input[id*="xCoord" i], input[id*="yCoord" i]');
              const hasAttackMode = !!document.querySelector('input[type="radio"][name="c"]');
              const body = (document.body?.innerText || '').toLowerCase();
              return hasCoords && hasAttackMode && body.includes('send troops');
            }
            """);
    }

    private async Task<bool> TryOpenSendTroopsTabAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before opening send troops tab.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const candidates = Array.from(document.querySelectorAll('a.tabItem, .tabItem, a[href*="build.php?t=2"], a[href*="t=2"]'));
              const target = candidates.find(node => {
                const text = (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const href = (node.getAttribute('href') || '').toLowerCase();
                return text.includes('send troops') || href.includes('build.php?t=2') || href.includes('t=2');
              });
              if (!target) return false;
              target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """);
    }

    private async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading farmlists.", cancellationToken);

        var rawRows = await _page.EvaluateAsync<FarmListRowJs[]>(
            """
            () => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const candidates = new Set();
              document.querySelectorAll('.listTitle').forEach((node) => candidates.add(node));
              if (candidates.size === 0) {
                document.querySelectorAll('.farmList, .farmlist').forEach((node) => candidates.add(node));
              }

              const rows = [];
              const seenByName = new Map();
              for (const candidate of candidates) {
                if (!candidate) continue;
                const titleTextNode = candidate.querySelector('.listTitleText') || candidate;
                const whole = normalize(titleTextNode.textContent);
                if (!whole) continue;
                if (whole.length > 300) continue;

                // True farm list title rows contain a delete icon button.
                if (!candidate.querySelector('img.del')) continue;

                const lowerWhole = whole.toLowerCase();
                if (lowerWhole.includes('building plans will be released') || lowerWhole.startsWith('server time')) {
                  continue;
                }

                let name =
                  normalize(candidate.querySelector('h1, h2, h3, h4, .title, .name, strong')?.textContent) ||
                  normalize(whole.split('\n')[0] || '') ||
                  whole;
                name = name
                  .replace(/\bdelete\b/ig, '')
                  .replace(/\(\d+\s*farms?\)/i, '')
                  .replace(/\s*start raid.*$/i, '')
                  .trim();
                if (!name) name = 'Farm list';
                if (name.length > 120) continue;

                const slashCountMatch = whole.match(/(\d+)\s*\/\s*(\d+)\s*farm/i);
                const parenCountMatch = whole.match(/\((\d+)\s*farms?\)/i);

                let active = 0;
                let total = 0;
                if (slashCountMatch) {
                  active = Number(slashCountMatch[1]);
                  total = Number(slashCountMatch[2]);
                } else if (parenCountMatch) {
                  active = Number(parenCountMatch[1]);
                  total = 120;
                }
                if (!Number.isFinite(active) || active < 0) active = 0;
                if (!Number.isFinite(total) || total < 0) total = 0;
                active = Math.min(active, 120);
                total = Math.min(total, 120);
                if (total > 0 && active > total) active = total;

                const container =
                  candidate.closest('.raidList, .listEntry, tr, li, article, section, .box') ||
                  candidate.parentElement ||
                  candidate;

                const timerText =
                  normalize(container.querySelector('span[id^="timerTop"]')?.textContent) ||
                  (normalize(container.querySelector('.button-content')?.textContent).match(/\d{1,3}:\d{2}(?::\d{2})?/) || [])[0] ||
                  '';

                const key = name.toLowerCase();
                const existing = seenByName.get(key);
                if (!existing) {
                  seenByName.set(key, { name, activeFarmCount: active, totalFarmCount: total, timerText });
                  continue;
                }

                seenByName.set(key, {
                  name,
                  activeFarmCount: Math.max(existing.activeFarmCount || 0, active),
                  totalFarmCount: Math.max(existing.totalFarmCount || 0, total),
                  timerText: (existing.timerText && existing.timerText.length > 0) ? existing.timerText : timerText
                });
              }

              for (const value of seenByName.values()) {
                rows.push(value);
              }

              return rows.slice(0, 200);
            }
            """);

        return rawRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => new FarmListOverview(
                Name: row.Name!,
                ActiveFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.ActiveFarmCount ?? 0)),
                TotalFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.TotalFarmCount ?? 0)),
                RemainingSeconds: ParseDurationToSeconds(row.TimerText)))
            .ToList();
    }

    private async Task WaitForDispatchLimitToClearAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while checking farm dispatch limit.", cancellationToken);

            var state = await _page.EvaluateAsync<FarmDispatchLimitStateJs>(
                """
                () => {
                  const parse = (raw) => {
                    const text = (raw || '').trim();
                    if (!text) return null;
                    const parts = text.split(':').map((p) => Number.parseInt(p.trim(), 10)).filter((n) => Number.isFinite(n));
                    if (parts.length === 2) return (parts[0] * 60) + parts[1];
                    if (parts.length === 3) return (parts[0] * 3600) + (parts[1] * 60) + parts[2];
                    return null;
                  };

                  const hasLimit = !!document.querySelector('.dispatchLimitError');
                  let minTimer = null;
                  document.querySelectorAll('span[id^="timerTop"]').forEach((node) => {
                    const seconds = parse(node.textContent || '');
                    if (seconds === null) return;
                    if (minTimer === null || seconds < minTimer) minTimer = seconds;
                  });

                  return { hasLimit, minTimerSeconds: minTimer };
                }
                """);

            if (state is null || !state.HasLimit)
            {
                return;
            }

            var waitSeconds = state.MinTimerSeconds is > 0
                ? Math.Max(1, state.MinTimerSeconds.Value)
                : 1;
            Notify($"Farm dispatch limit active. Waiting {waitSeconds}s before retry.");
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            await GotoAsync(Paths.FarmListPage, cancellationToken);
        }
    }

    private async Task<bool> TryClickFarmListSendNowAsync(string farmListName, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before sending farm list.", cancellationToken);
        var clicked = await _page.EvaluateAsync<bool>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return false;

              const tryReadListId = (root) => {
                if (!root) return null;
                const markAll = root.querySelector('input[id^="raidListMarkAll"]');
                if (markAll?.id) {
                  const match = markAll.id.match(/raidListMarkAll(\d+)/i);
                  if (match) return match[1];
                }

                const button = root.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                if (button?.id) {
                  const match = button.id.match(/startRaidBtnTop(\d+)/i);
                  if (match) return match[1];
                }
                if (button?.getAttribute('data-lid')) {
                  return button.getAttribute('data-lid');
                }

                const switchNode = root.querySelector('.openedClosedSwitch[onclick*="toggleList"]');
                const onclick = switchNode?.getAttribute('onclick') || '';
                const switchMatch = onclick.match(/toggleList\((\d+)\)/i);
                if (switchMatch) return switchMatch[1];

                return null;
              };

              let lid = null;
              const titleNodes = Array.from(document.querySelectorAll('.listTitle .listTitleText, .listTitleText'));
              for (const titleNode of titleNodes) {
                const titleName = normalizeListName(titleNode.textContent);
                if (titleName !== target) continue;

                const titleRoot = titleNode.closest('.listTitle') || titleNode.parentElement;
                lid = tryReadListId(titleRoot?.parentElement || titleRoot);
                if (!lid) {
                  lid = tryReadListId(titleRoot);
                }
                if (lid) break;
              }

              if (!lid) {
                const buttons = Array.from(document.querySelectorAll('button.startRaidButton[data-lid], button[id^="startRaidBtnTop"]'));
                for (const button of buttons) {
                  const row = button.closest('tr, li, article, section, .listEntry, .farmList, .farmlist, .slot, .box, .list, .raidList');
                  const rowName = normalizeListName(row?.querySelector('.listTitleText, h1, h2, h3, h4, .title, .name, strong')?.textContent || row?.textContent || '');
                  if (rowName === target) {
                    lid = button.getAttribute('data-lid') || ((button.id || '').match(/startRaidBtnTop(\d+)/i) || [])[1] || null;
                    if (lid) break;
                  }
                }
              }

              if (!lid) return false;

              const markAll = document.getElementById(`raidListMarkAll${lid}`) || document.querySelector(`input.markAll[id="raidListMarkAll${lid}"]`);
              if (markAll && markAll instanceof HTMLInputElement) {
                if (!markAll.checked) {
                  markAll.checked = true;
                }
                markAll.dispatchEvent(new Event('input', { bubbles: true }));
                markAll.dispatchEvent(new Event('change', { bubbles: true }));
              }

              const button = document.getElementById(`startRaidBtnTop${lid}`) || document.querySelector(`button.startRaidButton[data-lid="${lid}"]`);
              if (!button) return false;

              const className = (button.className || '').toLowerCase();
              if (button.disabled || className.includes('disabled')) return false;

              const text = normalize(button.textContent).toLowerCase();
              if (!text.includes('start raid') && !text.includes('send')) {
                return false;
              }

              button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """,
            farmListName);

        if (!clicked)
        {
            return false;
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking Start Raid.", cancellationToken);
        return true;
    }

    private async Task<int?> ReadFarmListTimerSecondsByNameAsync(string farmListName, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading farm list timer.", cancellationToken);
        var rawTimer = await _page.EvaluateAsync<string?>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return null;

              const titleNodes = Array.from(document.querySelectorAll('.listTitle .listTitleText, .listTitleText'));
              let lid = null;
              for (const titleNode of titleNodes) {
                const titleName = normalizeListName(titleNode.textContent);
                if (titleName !== target) continue;

                const root = titleNode.closest('.listTitle')?.parentElement || titleNode.closest('.listTitle') || titleNode.parentElement;
                const markAll = root?.querySelector('input[id^="raidListMarkAll"]');
                const markAllMatch = (markAll?.id || '').match(/raidListMarkAll(\d+)/i);
                if (markAllMatch) {
                  lid = markAllMatch[1];
                  break;
                }

                const btn = root?.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                const btnIdMatch = (btn?.id || '').match(/startRaidBtnTop(\d+)/i);
                if (btnIdMatch) {
                  lid = btnIdMatch[1];
                  break;
                }
                if (btn?.getAttribute('data-lid')) {
                  lid = btn.getAttribute('data-lid');
                  break;
                }
              }

              if (lid) {
                const byId = document.getElementById(`timerTop${lid}`);
                if (byId) return normalize(byId.textContent || '');
              }

              const rows = Array.from(document.querySelectorAll('tr, li, article, section, .listEntry, .farmList, .farmlist, .slot, .box, .list, .raidList'));
              for (const row of rows) {
                const text = normalizeListName(row.querySelector('.listTitleText, h1, h2, h3, h4, .title, .name, strong')?.textContent || row.textContent || '');
                if (text !== target) continue;

                const timer = row.querySelector('span[id^="timerTop"]');
                if (timer) return normalize(timer.textContent || '');

                const content = row.querySelector('.button-content');
                if (!content) return null;
                const match = normalize(content.textContent || '').match(/\d{1,3}:\d{2}(?::\d{2})?/);
                return match ? match[0] : null;
              }

              return null;
            }
            """,
            farmListName);

        return ParseDurationToSeconds(rawTimer);
    }

    private async Task<NatarCoordinateJs?> ReadFirstNatarFarmCoordinateAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.PlayerProfileNatars, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Natars profile.", cancellationToken);
        await EnsureLoggedInAsync();

        var coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        if (coordinates.Length > 0)
        {
            return coordinates[0];
        }

        await GotoAsync(Paths.Statistics100, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening statistics farms page.", cancellationToken);
        await EnsureLoggedInAsync();

        await _page.EvaluateAsync(
            """
            () => {
              const links = Array.from(document.querySelectorAll('a[href*="spieler.php?uid=3"]'));
              const link = links.find(node => ((node.textContent || '').toLowerCase().includes('natar'))) || links[0];
              if (link) {
                link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              }
            }
            """);
        await Task.Delay(400, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while navigating to Natars profile.", cancellationToken);

        coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        return coordinates.FirstOrDefault();
    }

    private async Task<List<NatarCoordinateJs>> ReadNatarFarmCoordinatesCachedAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var selectionMode = ResolveNatarVillageSelectionMode();
        var cacheKey = $"{ServerUrl}::{AccountName}::{selectionMode}";
        if (!forceRefresh)
        {
            lock (NatarCacheSync)
            {
                if (CachedNatarCoordinatesByAccount.TryGetValue(cacheKey, out var existing) && existing.Count > 0)
                {
                    Notify($"Using cached Natar farms list ({existing.Count}).");
                    return [.. existing];
                }
            }

            if (_natarFarmCacheStore.TryLoad(AccountName, out var persisted, ServerUrl, selectionMode)
                && persisted is not null
                && persisted.Coordinates.Count > 0)
            {
                var restored = persisted.Coordinates
                    .Select(item => new NatarCoordinateJs { X = item.X, Y = item.Y, VillageName = item.VillageName })
                    .ToList();
                lock (NatarCacheSync)
                {
                    CachedNatarCoordinatesByAccount[cacheKey] = [.. restored];
                }

                Notify($"Using persisted Natar farms list ({restored.Count}).");
                return restored;
            }
        }

        await GotoAsync(Paths.PlayerProfileNatars, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Natars profile.", cancellationToken);
        await EnsureLoggedInAsync();

        var coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        if (coordinates.Length <= 0)
        {
            await GotoAsync(Paths.Statistics100, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening statistics farms page.", cancellationToken);
            await EnsureLoggedInAsync();
            await _page.EvaluateAsync(
                """
                () => {
                  const links = Array.from(document.querySelectorAll('a[href*="spieler.php?uid=3"]'));
                  const link = links.find(node => ((node.textContent || '').toLowerCase().includes('natar'))) || links[0];
                  if (link) {
                    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                  }
                }
                """);
            await Task.Delay(400, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while navigating to Natars profile.", cancellationToken);
            coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        }

        var cached = coordinates
            .Where(item => item.X.HasValue && item.Y.HasValue)
            .GroupBy(item => $"{item.X}|{item.Y}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ToList();
        lock (NatarCacheSync)
        {
            CachedNatarCoordinatesByAccount[cacheKey] = [.. cached];
        }

        if (cached.Count > 0)
        {
            var changed = _natarFarmCacheStore.Save(new NatarFarmCacheSnapshot(
                SchemaVersion: 1,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: AccountName,
                ServerUrl: ServerUrl,
                SelectionMode: selectionMode,
                Coordinates: cached
                    .Where(item => item.X.HasValue && item.Y.HasValue)
                    .Select(item => new NatarFarmCoordinate(item.X!.Value, item.Y!.Value, item.VillageName))
                    .ToList()));

            Notify(changed
                ? $"Scanned Natar farms list and saved {cached.Count} entries."
                : $"Scanned Natar farms list and confirmed existing {cached.Count} cached entries.");
        }
        else
        {
            Notify("Scanned Natar farms list but found no entries.");
        }

        return cached;
    }

    private async Task<NatarCoordinateJs[]> ReadNatarFarmCoordinatesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading Natar coordinates.", cancellationToken);
        var includeAllVillages = string.Equals(ResolveNatarVillageSelectionMode(), "all_villages", StringComparison.OrdinalIgnoreCase);
        return await _page.EvaluateAsync<NatarCoordinateJs[]>(
            """
            (includeAllVillages) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const targetVillageName = 'Natar farm village';
              const parsePair = (text) => {
                const normalized = clean(text);
                if (!normalized) return null;
                const match = normalized.match(/(-?\d+)\s*[|/]\s*(-?\d+)/);
                if (!match) return null;
                const x = Number.parseInt(match[1], 10);
                const y = Number.parseInt(match[2], 10);
                if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
                return { x, y };
              };

              const seen = new Set();
              const rows = [];
              for (const row of document.querySelectorAll('tr, li, article, section, .row')) {
                const hasVillageAnchor = !!row.querySelector('a[href*="karte.php"]');
                if (!hasVillageAnchor) continue;
                const villageNameNode =
                  row.querySelector('td.vil a, .vil a, .village a, a[href*="dorf1.php"], a[href*="newdid="], a[href*="profile.php"]');
                const villageNameFromRow = clean(villageNameNode?.textContent || '');
                const names = Array.from(row.querySelectorAll('a, .name, .village, .vil, td, span'))
                  .map(node => clean(node.textContent || ''))
                  .filter(Boolean);
                const hasTargetVillageName = names.some(text => text === targetVillageName);
                if (!includeAllVillages && !hasTargetVillageName) continue;
                const rowText = clean(row.textContent || '');
                const parsed = parsePair(rowText);
                if (!parsed) continue;
                const key = `${parsed.x}|${parsed.y}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const villageName = villageNameFromRow
                  || (hasTargetVillageName
                    ? targetVillageName
                    : (names.find(text => text !== `${parsed.x}|${parsed.y}` && text !== `${parsed.x}/${parsed.y}`) || ''));
                rows.push({ x: parsed.x, y: parsed.y, villageName });
              }

              if (rows.length > 0) return rows;

              for (const anchor of document.querySelectorAll('a[href*="karte.php"]')) {
                const row = anchor.closest('tr, li, article, section, .row');
                const rowText = clean(row?.textContent || '');
                if (!includeAllVillages && !rowText.includes(targetVillageName)) continue;
                const parsed = parsePair(anchor.textContent || '');
                if (!parsed) continue;
                const key = `${parsed.x}|${parsed.y}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const villageName = includeAllVillages ? (anchor.textContent || '').trim() : targetVillageName;
                rows.push({ x: parsed.x, y: parsed.y, villageName });
              }

              return rows;
            }
            """,
            includeAllVillages);
    }

    private string ResolveNatarVillageSelectionMode()
    {
        return string.Equals(_config.NatarVillageSelection, "all_villages", StringComparison.OrdinalIgnoreCase)
            ? "all_villages"
            : "farm_villages";
    }

    private async Task<string?> TryResolveFarmListSlotIdByNameAsync(string farmListName, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while resolving farm list slot.", cancellationToken);
        return await _page.EvaluateAsync<string?>(
            """
            (targetName) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const normalizeListName = (value) => normalize(value)
                .replace(/\(\d+\s*farms?\)/i, '')
                .replace(/\bdelete\b/ig, '')
                .trim()
                .toLowerCase();
              const target = normalizeListName(targetName);
              if (!target) return null;

              const tryReadListId = (root) => {
                if (!root) return null;
                const markAll = root.querySelector('input[id^="raidListMarkAll"]');
                if (markAll?.id) {
                  const match = markAll.id.match(/raidListMarkAll(\d+)/i);
                  if (match) return match[1];
                }

                const button = root.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid], button[onclick*="showSlot"][onclick*="lid="]');
                if (button?.id) {
                  const match = button.id.match(/startRaidBtnTop(\d+)/i);
                  if (match) return match[1];
                }
                if (button?.getAttribute('data-lid')) {
                  return button.getAttribute('data-lid');
                }
                const onclick = button?.getAttribute('onclick') || '';
                const onclickMatch = onclick.match(/[?&]lid=(\d+)/i) || onclick.match(/lid=(\d+)/i);
                if (onclickMatch) return onclickMatch[1];

                return null;
              };

              const titleNodes = Array.from(document.querySelectorAll('.listTitle .listTitleText, .listTitleText, .listTitle, h1, h2, h3, h4, .title, .name, strong'));
              for (const titleNode of titleNodes) {
                const titleName = normalizeListName(titleNode.textContent);
                if (titleName !== target) continue;

                const titleRoot = titleNode.closest('.listTitle') || titleNode.parentElement;
                const lid = tryReadListId(titleRoot?.parentElement || titleRoot) || tryReadListId(titleRoot);
                if (lid) return lid;
              }

              return null;
            }
            """,
            farmListName);
    }

    private async Task<AddRaidSaveOutcome> TryFillAddRaidFormAndSaveAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before filling Add Raid form.", cancellationToken);
        var saved = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const norm = (value) => normalize(value).toLowerCase();
              const setValue = (input, value) => {
                if (!input) return false;
                input.focus();
                input.value = String(value);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };

              const all = document;
              const buttons = Array.from(all.querySelectorAll('button, input[type="submit"], a'));
              const saveButton = buttons.find(node => {
                const text = `${node.textContent || ''} ${node.getAttribute('value') || ''} ${node.getAttribute('title') || ''}`.toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return !disabled && (text.includes('save') || text.includes('spara'));
              });
              if (!saveButton) return false;

              const root = saveButton.closest('form, .content, .box, #content') || document;
              const selects = Array.from(root.querySelectorAll('select'));
              const textInputs = Array.from(root.querySelectorAll('input:not([type="hidden"])'));

              const findInput = (patterns, fallback = null) => {
                for (const pattern of patterns) {
                  const candidate = textInputs.find(node => {
                    const id = (node.id || '').toLowerCase();
                    const name = (node.getAttribute('name') || '').toLowerCase();
                    const type = (node.getAttribute('type') || 'text').toLowerCase();
                    if (type !== 'text' && type !== 'number' && type !== '') return false;
                    return id.includes(pattern) || name === pattern || name.includes(pattern);
                  });
                  if (candidate) return candidate;
                }
                return fallback;
              };

              const xInput = findInput(['xcoord', 'coordx', 'x']);
              const yInput = findInput(['ycoord', 'coordy', 'y']);
              if (!xInput || !yInput) return false;
              if (!setValue(xInput, args.x) || !setValue(yInput, args.y)) return false;

              const listSelect = selects.find(select => Array.from(select.options || []).some(option => norm(option.textContent || '') === norm(args.farmListName)));
              if (listSelect) {
                const option = Array.from(listSelect.options || []).find(opt => norm(opt.textContent || '') === norm(args.farmListName));
                if (option) {
                  listSelect.value = option.value;
                  listSelect.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              const troopSelect = selects.find(select => Array.from(select.options || []).some(option => norm(option.textContent || '') === norm(args.troopType)));
              if (troopSelect) {
                const option = Array.from(troopSelect.options || []).find(opt => norm(opt.textContent || '') === norm(args.troopType));
                if (option) {
                  troopSelect.value = option.value;
                  troopSelect.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              let countInput = textInputs.find(node => {
                if (node === xInput || node === yInput) return false;
                const id = (node.id || '').toLowerCase();
                const name = (node.getAttribute('name') || '').toLowerCase();
                return id.includes('count') || id.includes('amount') || name.includes('count') || name.includes('amount');
              });
              if (!countInput) {
                countInput = textInputs.find(node => node !== xInput && node !== yInput);
              }
              if (!countInput) return false;
              if (!setValue(countInput, args.troopCount)) return false;

              saveButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """,
            new
            {
                farmListName,
                troopType,
                troopCount,
                x,
                y,
            });

        if (!saved)
        {
            return AddRaidSaveOutcome.Failed;
        }

        await Task.Delay(350, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after saving new raid.", cancellationToken);
        await EnsureLoggedInAsync();

        var saveState = await _page.EvaluateAsync<string>(
            """
            () => {
              const text = (document.body?.innerText || '').replace(/\s+/g, ' ').trim();
              if (text.includes('This village is already in the selected farm list.')) return 'already';
              if (text.toLowerCase().includes('success') || text.toLowerCase().includes('saved')) return 'saved';
              return 'unknown';
            }
            """);

        if (string.Equals(saveState, "already", StringComparison.OrdinalIgnoreCase))
        {
            return AddRaidSaveOutcome.AlreadyInList;
        }

        return AddRaidSaveOutcome.Added;
    }

    private async Task<ManualAttackSendResult> TrySendManualAttackAsync(
        string troopType,
        int troopIndex,
        int troopCount,
        int troopVariancePercent,
        int x,
        int y,
        bool raidAttack,
        CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before filling manual farming form.", cancellationToken);
        var randomizedTroopCount = ResolveRandomizedTroopCount(troopCount, troopVariancePercent);
        var minimumAcceptedTroopCount = Math.Max(1, (int)Math.Ceiling(randomizedTroopCount * ManualFarmingMinimumTroopRatio));
        var fieldToken = $"t{troopIndex}";
        var availableTroopCount = await WaitForAvailableTroopsAsync(fieldToken, troopType, cancellationToken);
        if (availableTroopCount == 0)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.StoppedByNoTroopsAlarm,
                0,
                0,
                minimumAcceptedTroopCount);
        }

        var troopCountToSend = randomizedTroopCount;

        if (availableTroopCount.HasValue)
        {
            troopCountToSend = Math.Min(randomizedTroopCount, Math.Max(0, availableTroopCount.Value));
            if (troopCountToSend < minimumAcceptedTroopCount)
            {
                return new ManualAttackSendResult(
                    ManualAttackSendStatus.SkippedLowTroops,
                    availableTroopCount.Value,
                    troopCountToSend,
                    minimumAcceptedTroopCount);
            }
        }

        await FillFirstAvailableAsync(["input[name='x']", "input[name='xCoord']", "input[id*='xCoord' i]"], x.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await FillFirstAvailableAsync(["input[name='y']", "input[name='yCoord']", "input[id*='yCoord' i]"], y.ToString(CultureInfo.InvariantCulture), cancellationToken);

        var troopInputFilled = await TryFillTroopInputAsync(fieldToken, troopType, troopCountToSend, cancellationToken);
        if (!troopInputFilled)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0,
                0,
                minimumAcceptedTroopCount);
        }

        var attackModeSelected = await TrySelectAttackModeAsync(raidAttack, cancellationToken);
        if (!attackModeSelected)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        var firstConfirmClicked = await TryClickConfirmButtonAsync(cancellationToken);
        if (!firstConfirmClicked)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        var confirmationPageReady = await WaitForManualAttackConfirmationPageAsync(cancellationToken);
        if (!confirmationPageReady)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        var secondConfirmClicked = await TryClickConfirmButtonAsync(cancellationToken);

        if (!secondConfirmClicked)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        await WaitForManualAttackCompletionAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after sending manual farming attack.", cancellationToken);
        await EnsureLoggedInAsync();
        return new ManualAttackSendResult(
            ManualAttackSendStatus.Sent,
            availableTroopCount ?? troopCountToSend,
            troopCountToSend,
            minimumAcceptedTroopCount);
    }

    private async Task<int?> ReadAvailableTroopCountAsync(string fieldToken, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading available troops.", cancellationToken);
        try
        {
            var result = await _page.EvaluateAsync<int?>(
                """
                (fieldToken) => {
                  const token = (fieldToken || '').toLowerCase();
                  const anchors = Array.from(document.querySelectorAll('a[onclick*=".value="]'));
                  for (const anchor of anchors) {
                    const onclick = anchor.getAttribute('onclick') || '';
                    const match = onclick.match(/(?:snd|document\.snd)\.([A-Za-z0-9_]+)\.value\s*=\s*(\d+)/i);
                    if (!match) continue;
                    if ((match[1] || '').toLowerCase() !== token) continue;
                    const parsed = Number.parseInt(match[2], 10);
                    if (Number.isFinite(parsed)) return parsed;
                  }

                  const input = document.querySelector(`input[name="${fieldToken}"], input[id$="${fieldToken}"], input[name$="[${fieldToken}]"]`);
                  if (!input) return null;

                  const maxValue = input.getAttribute('max') || '';
                  const maxParsed = Number.parseInt((maxValue || '').replace(/[^\d]/g, ''), 10);
                  if (Number.isFinite(maxParsed) && maxParsed > 0) return maxParsed;

                  return null;
                }
                """,
                fieldToken);

            return result > 0 ? result : null;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while reading available troops. Continuing without exact availability.");
            return null;
        }
    }

    private async Task<int?> WaitForAvailableTroopsAsync(string fieldToken, string troopType, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ManualFarmingNoTroopsRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var availableTroopCount = await ReadAvailableTroopCountAsync(fieldToken, cancellationToken);
            if (availableTroopCount.GetValueOrDefault() > 0)
            {
                return availableTroopCount;
            }

            if (attempt >= ManualFarmingNoTroopsRetryAttempts)
            {
                return 0;
            }

            Notify(
                $"Available {troopType.Trim()}: {FormatLargeCount(availableTroopCount.GetValueOrDefault())}. " +
                $"Waiting {ManualFarmingNoTroopsRetryWaitSeconds}s before retry {attempt + 1}/{ManualFarmingNoTroopsRetryAttempts}. " +
                $"queue_wait_seconds={ManualFarmingNoTroopsRetryWaitSeconds}");
            await Task.Delay(TimeSpan.FromSeconds(ManualFarmingNoTroopsRetryWaitSeconds), cancellationToken);
        }

        return 0;
    }

    private async Task<bool> TryFillTroopInputAsync(string fieldToken, string troopType, int troopCountToSend, CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            $"input[name='troops[0][{fieldToken}]']",
            $"input[name$='[{fieldToken}]']",
            $"input[name='{fieldToken}']",
            $"input[id$='{fieldToken}']",
        };

        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            await RetryAsync($"fill troop input {selector}", async () =>
            {
                await locator.FillAsync(troopCountToSend.ToString(CultureInfo.InvariantCulture), new LocatorFillOptions { Timeout = _config.TimeoutMs });
            }, cancellationToken: cancellationToken);
            return true;
        }

        var fallbackFilled = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const inputs = Array.from(document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])'));
              const candidate = inputs.find(node => {
                const text = normalize(`${node.closest('tr, td, div, label, li, .troop_details, .details')?.textContent || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                return text.includes(normalize(args.troopType));
              });
              if (!candidate) return false;
              candidate.focus();
              candidate.value = String(args.count);
              candidate.dispatchEvent(new Event('input', { bubbles: true }));
              candidate.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            }
            """,
            new { troopType, count = troopCountToSend });

        return fallbackFilled;
    }

    private async Task<bool> TrySelectAttackModeAsync(bool raidAttack, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while selecting attack mode.", cancellationToken);
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                (raidAttack) => {
                  const radioButtons = Array.from(document.querySelectorAll('input[type="radio"][name="c"]'));
                  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const radio = radioButtons.find(node => {
                    const value = (node.getAttribute('value') || '').trim();
                    const label = normalize(node.parentElement?.textContent || node.closest('label')?.textContent || '');
                    if (raidAttack) return value === '4' || label.includes('raid');
                    return label.includes('normal attack') || (value === '3') || (label.includes('attack') && !label.includes('raid') && !label.includes('reinforcement'));
                  });
                  if (!radio) return false;
                  radio.checked = true;
                  radio.dispatchEvent(new Event('input', { bubbles: true }));
                  radio.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
                """,
                raidAttack);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while selecting attack mode.");
            return false;
        }
    }

    private async Task<bool> WaitForManualAttackConfirmationPageAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Continue polling during navigation.
            }

            var confirmReady = await HasAnySelectorAsync(
            [
                ".button-container:has(.button-content:text-is('Confirm'))",
                ".button-content:text-is('Confirm')",
                "button:has-text('Confirm')",
                "a:has-text('Confirm')",
            ]);

            if (confirmReady)
            {
                return true;
            }

            if (attempt < 12)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        return false;
    }

    private async Task WaitForManualAttackCompletionAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 1200),
                });
            }
            catch (TimeoutException)
            {
                // The next loop iteration will recover by reopening Send Troops if needed.
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Continue polling during navigation.
            }

            if (await IsSendTroopsPageAsync(cancellationToken))
            {
                return;
            }

            var confirmStillVisible = await HasAnySelectorAsync(
            [
                ".button-container:has(.button-content:text-is('Confirm'))",
                ".button-content:text-is('Confirm')",
                "button:has-text('Confirm')",
                "a:has-text('Confirm')",
            ]);

            if (!confirmStillVisible)
            {
                return;
            }

            if (attempt < 4)
            {
                await Task.Delay(120, cancellationToken);
            }
        }
    }

    private async Task<bool> TryClickConfirmButtonAsync(CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            ".button-container:has(.button-content:text-is('Confirm'))",
            ".button-content:text-is('Confirm')",
            "button:has-text('Confirm')",
            "input[type='submit'][value*='Confirm' i]",
            "input[type='button'][value*='Confirm' i]",
            "a:has-text('Confirm')",
        };

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var selector in selectors)
                {
                    var locator = _page.Locator(selector).First;
                    if (await locator.CountAsync() == 0)
                    {
                        continue;
                    }

                    await RetryAsync($"click confirm selector {selector}", async () =>
                    {
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                    }, cancellationToken: cancellationToken);

                    await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking confirm.", cancellationToken);
                    return true;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify($"Confirm page navigated during attempt {attempt}/4. Retrying...");
            }
            catch (TimeoutException) when (attempt < 4)
            {
                Notify($"Confirm button timed out on attempt {attempt}/4. Retrying...");
            }

            if (attempt < 4)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        return false;
    }

    private enum AddRaidSaveOutcome
    {
        Failed = 0,
        Added = 1,
        AlreadyInList = 2,
    }

    private sealed record ManualAttackSendResult(
        ManualAttackSendStatus Status,
        int AvailableTroopCount,
        int SentTroopCount,
        int MinimumAcceptedTroopCount);

    private enum ManualAttackSendStatus
    {
        Failed = 0,
        SkippedLowTroops = 1,
        Sent = 2,
        StoppedByNoTroopsAlarm = 3,
    }

    private static string FormatLargeCount(int value)
    {
        return Math.Max(0, value).ToString("#,0", CultureInfo.InvariantCulture);
    }

    private static int ResolveRandomizedTroopCount(int troopCount, int troopVariancePercent)
    {
        var normalizedTroopCount = Math.Max(1, troopCount);
        var normalizedVariancePercent = troopVariancePercent switch
        {
            0 or 5 or 10 or 20 or 50 => troopVariancePercent,
            _ => 10,
        };

        if (normalizedVariancePercent <= 0)
        {
            return normalizedTroopCount;
        }

        var min = Math.Max(1, (int)Math.Floor(normalizedTroopCount * (100 - normalizedVariancePercent) / 100d));
        var max = Math.Max(min, (int)Math.Ceiling(normalizedTroopCount * (100 + normalizedVariancePercent) / 100d));
        return Random.Shared.Next(min, max + 1);
    }

    private async Task ClickDetectedUpgradeCandidateAsync(int slotId, int? candidateIndex, CancellationToken cancellationToken)
    {
        if (candidateIndex is null || candidateIndex < 0)
        {
            throw new InvalidOperationException($"Upgrade candidate index is missing for slot {slotId}.");
        }

        await EnsureExpectedBuildSlotPageAsync(slotId, "click detected upgrade candidate");

        var locator = _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(candidateIndex.Value);
        await RetryAsync($"click detected upgrade candidate index {candidateIndex.Value} for slot {slotId}", async () =>
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
        }, cancellationToken: cancellationToken);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking detected upgrade candidate.", cancellationToken);
    }

    internal static UpgradeAttemptOutcome ParseUpgradeOutcome(string? value)
    {
        return value?.Trim() switch
        {
            "CanUpgrade" => UpgradeAttemptOutcome.CanUpgrade,
            "BlockedByResources" => UpgradeAttemptOutcome.BlockedByResources,
            "BlockedByQueue" => UpgradeAttemptOutcome.BlockedByQueue,
            "BlockedByMaxLevel" => UpgradeAttemptOutcome.BlockedByMaxLevel,
            _ => UpgradeAttemptOutcome.BlockedUnknown,
        };
    }

    private async Task<int?> ReadUpgradeDurationSecondsOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading upgrade duration.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isDuration = (value) => /\d{1,3}\s*:\s*\d{1,2}(?:\s*:\s*\d{1,2})?/.test(value) || /\b\d+\s*(?:min|minute|sec|second)s?\b/i.test(value);
              const found = [];
              const seen = new Set();
              const pushIfDuration = (value) => {
                const text = clean(value);
                if (!text || !isDuration(text) || seen.has(text)) return;
                seen.add(text);
                found.push(text);
              };

              // Prefer explicit upgrade duration element (same area as the upgrade button).
              const directSelectors = [
                '.inlineIcon.duration .value',
                '.inlineIcon.duration .timer',
                '.inlineIcon.duration'
              ];
              for (const selector of directSelectors) {
                for (const node of document.querySelectorAll(selector)) {
                  pushIfDuration(node.textContent);
                }
              }

              const blocks = [
                ...document.querySelectorAll('.upgradeBuilding, .contract, .contractWrapper, .build_details, #contract, form[action*="build.php"]')
              ];
              for (const block of blocks) {
                const nodes = block.querySelectorAll('.timer, .countdown, .value, [counting="down"], [id^="timer"]');
                for (const node of nodes) {
                  pushIfDuration(node.textContent);
                }
              }

              return JSON.stringify(found);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(rawJson) ?? new List<string>();
        if (raw.Count == 0)
        {
            return null;
        }

        var candidateSeconds = raw
            .Select(ParseDurationToSeconds)
            .Where(value => value.HasValue && value.Value > 0)
            .Select(value => value!.Value)
            .ToList();
        if (candidateSeconds.Count == 0)
        {
            return null;
        }

        // Prefer the shortest detected upgrade timer; first-hit can be an unrelated countdown.
        return candidateSeconds.Min();
    }

    private async Task CaptureFailureArtifactsAsync(string label, CancellationToken cancellationToken)
    {
        if (_page.IsClosed)
        {
            return;
        }

        var safeLabel = SafePathSegment(label);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var diagnosticsRoot = Path.Combine(
            _projectRoot,
            "temp_build_out",
            "diagnostics",
            DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(diagnosticsRoot);

        var screenshotPath = Path.Combine(diagnosticsRoot, $"{stamp}-{safeLabel}.png");
        var htmlPath = Path.Combine(diagnosticsRoot, $"{stamp}-{safeLabel}.html");

        try
        {
            await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true,
            });
            var html = await _page.ContentAsync();
            await File.WriteAllTextAsync(htmlPath, html, cancellationToken);
            Notify($"Captured diagnostics: screenshot='{screenshotPath}', html='{htmlPath}'.");
        }
        catch (Exception ex)
        {
            Notify($"Could not capture diagnostics for '{label}': {ex.Message}");
        }
    }

    internal static string SafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "artifact";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "artifact"
            : sanitized;
    }

    private string? ResolveUrl(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(new Uri(_config.BaseUrl.TrimEnd('/') + "/"), href, out var combined))
        {
            return combined.ToString();
        }

        return href;
    }

    private void RecordServerTime(string? dateHeader)
    {
        if (string.IsNullOrWhiteSpace(dateHeader))
        {
            return;
        }

        if (!DateTimeOffset.TryParse(
                dateHeader,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return;
        }

        _serverTimeUtc = parsed.ToUniversalTime();
    }

    private void Notify(string message)
    {
        message = NormalizeStartedMessage(message);
        _statusCallback?.Invoke(message);
    }

    private void LogFunctionStarted([CallerMemberName] string? memberName = null)
    {
        if (string.IsNullOrWhiteSpace(memberName))
        {
            return;
        }

        Notify($"[{memberName}] started");
    }

    private static string NormalizeStartedMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.StartsWith('['))
        {
            return message;
        }

        var startedIndex = message.IndexOf(" started", StringComparison.Ordinal);
        if (startedIndex <= 0)
        {
            return message;
        }

        var tokenEnd = message.IndexOf(' ');
        if (tokenEnd <= 0)
        {
            tokenEnd = startedIndex;
        }

        var memberName = message[..tokenEnd].Trim();
        if (string.IsNullOrWhiteSpace(memberName))
        {
            return message;
        }

        return $"[{memberName}]{message[tokenEnd..]}";
    }

    internal enum UpgradeAttemptOutcome
    {
        CanUpgrade = 0,
        BlockedByResources = 1,
        BlockedByQueue = 2,
        BlockedByMaxLevel = 3,
        BlockedUnknown = 4,
    }

    private sealed record UpgradeAttemptResult(
        UpgradeAttemptOutcome Outcome,
        string Reason,
        int? DetectedMaxLevel,
        int? QueueWaitSeconds,
        int? CandidateIndex,
        string DebugSummary);

    private sealed record UpgradeProgressResult(
        bool Advanced,
        bool QueuedOrInProgress,
        string Evidence);

    private sealed class UpgradeActionabilityJs
    {
        [JsonPropertyName("outcome")]
        public string? Outcome { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("detectedMaxLevel")]
        public int? DetectedMaxLevel { get; init; }

        [JsonPropertyName("queueWaitSeconds")]
        public int? QueueWaitSeconds { get; init; }

        [JsonPropertyName("candidateIndex")]
        public int? CandidateIndex { get; init; }

        [JsonPropertyName("summary")]
        public List<UpgradeCandidateSummaryJs>? Summary { get; init; }
    }

    private sealed class UpgradeCandidateSummaryJs
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("classes")]
        public string? Classes { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }

        [JsonPropertyName("inUpgradeContainer")]
        public bool InUpgradeContainer { get; init; }
    }

    private sealed class ResourceFieldJs
    {
        [JsonPropertyName("slotId")]
        public int? SlotId { get; init; }

        [JsonPropertyName("fieldType")]
        public string? FieldType { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("href")]
        public string? Href { get; init; }
    }

    private sealed class BuildingJs
    {
        [JsonPropertyName("slotId")]
        public int? SlotId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("gid")]
        public int? Gid { get; init; }

        [JsonPropertyName("href")]
        public string? Href { get; init; }
    }

    private sealed class BuildQueueJs
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("timeLeft")]
        public string? TimeLeft { get; init; }
    }

    private sealed class ActiveConstructionJs
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("timeLeftSeconds")]
        public int? TimeLeftSeconds { get; init; }

        [JsonPropertyName("finishAtText")]
        public string? FinishAtText { get; init; }
    }

    private sealed class ServerBuildChoiceJs
    {
        [JsonPropertyName("gid")]
        public int? Gid { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("available")]
        public bool Available { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    private sealed class FarmListRowJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("activeFarmCount")]
        public int? ActiveFarmCount { get; init; }

        [JsonPropertyName("totalFarmCount")]
        public int? TotalFarmCount { get; init; }

        [JsonPropertyName("timerText")]
        public string? TimerText { get; init; }
    }

    private sealed class FarmDispatchLimitStateJs
    {
        [JsonPropertyName("hasLimit")]
        public bool HasLimit { get; init; }

        [JsonPropertyName("minTimerSeconds")]
        public int? MinTimerSeconds { get; init; }
    }

    private sealed class NatarCoordinateJs
    {
        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }

        [JsonPropertyName("villageName")]
        public string? VillageName { get; init; }
    }

    private sealed class PlayerProfileVillageRowJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("isCapital")]
        public bool IsCapital { get; init; }

        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }

        [JsonPropertyName("population")]
        public int? Population { get; init; }

        [JsonPropertyName("cropFields")]
        public int? CropFields { get; init; }
    }

    private sealed class SidebarVillageJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("isCapital")]
        public bool? IsCapital { get; init; }
    }
}
