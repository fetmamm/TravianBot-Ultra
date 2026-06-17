using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
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
    private const int OfficialFarmListCapacity = 100;
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
    private readonly HeroAttributeSnapshotStore _heroAttributeSnapshotStore;
    private readonly NatarFarmCacheStore _natarFarmCacheStore;
    private readonly ICaptchaAutoSolver? _captchaAutoSolver;
    private readonly Action<string>? _statusCallback;
    private DateTimeOffset? _serverTimeUtc;
    private DateTimeOffset _lastManualVerificationScreenshotAt = DateTimeOffset.MinValue;
    private string? _cachedTribe;
    private readonly TravianSessionCache _session;
    private static readonly TimeSpan TribePlusCacheTtl = TimeSpan.FromMinutes(10);
    // These caches are backed by the shared session cache (_session) so they survive across the
    // short-lived TravianClient instances created per operation for the same browser session.
    private bool? _cachedTravianPlusActive { get => _session.CachedTravianPlusActive; set => _session.CachedTravianPlusActive = value; }
    private DateTimeOffset _cachedTribePlusAt { get => _session.CachedTribePlusAt; set => _session.CachedTribePlusAt = value; }
    private bool? _cachedGoldClubEnabled { get => _session.CachedGoldClubEnabled; set => _session.CachedGoldClubEnabled = value; }
    private int? _cachedGold { get => _session.CachedGold; set => _session.CachedGold = value; }
    private int? _cachedSilver { get => _session.CachedSilver; set => _session.CachedSilver = value; }
    private DateTimeOffset _cachedCurrencyAt { get => _session.CachedCurrencyAt; set => _session.CachedCurrencyAt = value; }
    private string? _sessionTribe { get => _session.SessionTribe; set => _session.SessionTribe = value; }

    // Short-lived cache for ReadActiveConstructionsAsync. One upgrade-to-max iteration makes
    // several pre-click reads of the SAME dorf2 page state (e.g. ReadHighestKnownQueuedBuildingLevel
    // then CheckQueueOrDefer/EvaluateConstructionSlots). At 800ms these missed on slightly slow pages
    // and re-fetched, doubling the network round-trips per upgraded level. The TTL is sized to span a
    // single iteration's pre-click window so those collapse into one read. This is safe: every caller
    // that needs FRESH state after a click calls InvalidateActiveConstructionsCache() first
    // (e.g. WaitForBuildingLevelAdvanceAsync), and GotoAsync/ReloadOrGotoAsync invalidate automatically
    // on every navigation — so the cache is always re-seeded fresh at the top of each iteration.
    private IReadOnlyList<ActiveConstruction>? _cachedActiveConstructions;
    private DateTimeOffset _cachedActiveConstructionsAt = DateTimeOffset.MinValue;
    private bool _cachedActiveConstructionsFromOverview;
    private bool _lastActiveConstructionsFromOverview;
    private static readonly TimeSpan ActiveConstructionsCacheTtl = TimeSpan.FromMilliseconds(2500);

    internal void InvalidateActiveConstructionsCache()
    {
        _cachedActiveConstructions = null;
        _cachedActiveConstructionsFromOverview = false;
        _lastActiveConstructionsFromOverview = false;
    }

    private sealed class CaptchaClipRegion
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    private sealed record UiSyncVillage(
        string Name,
        string? Url,
        bool? IsCapital,
        int? CoordX,
        int? CoordY,
        int? Population,
        int? CropFields);
    private sealed record UiSyncSnapshot(int? Gold, int? Silver, string ActiveVillage, IReadOnlyList<UiSyncVillage> Villages);

    // Session-level cache for the villages list. Spieler.php is expensive to load, but the
    // prefer-cache path only re-reads the lightweight current-page sidebar (no navigation), so a
    // short TTL lets the ~16s background refresh pick up renamed/new villages promptly while still
    // sharing one read across rapid successive calls.
    private static readonly TimeSpan VillagesCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EnsureLoggedInMinInterval = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan UiSyncMinInterval = TimeSpan.FromSeconds(20);
    private static readonly object ResourceStatusCacheSync = new();
    private static readonly object HeroAttributeSnapshotCacheSync = new();
    private static readonly Dictionary<string, CachedVillageResourceSnapshot> CachedVillageResourceSnapshotsByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HeroAttributeSnapshot> CachedHeroAttributeSnapshotsByKey = new(StringComparer.OrdinalIgnoreCase);
    // Villages list + population cache are backed by the shared session cache (_session) so the
    // spieler.php read survives across the per-operation clients (no duplicate startup navigation)
    // and the population baseline persists between operations.
    private List<Village>? _cachedVillages { get => _session.CachedVillages; set => _session.CachedVillages = value; }
    private DateTimeOffset _cachedVillagesAt { get => _session.CachedVillagesAt; set => _session.CachedVillagesAt = value; }
    // Tracks whether the villages list was read from the server (spieler.php) WITH population.
    // Reset to MinValue on a real village switch to force the next ReadVillagesAsync to re-read
    // population from spieler; otherwise the cache (kept current by incremental updates) is served.
    private DateTimeOffset _cachedVillagesPopulationAt { get => _session.CachedVillagesPopulationAt; set => _session.CachedVillagesPopulationAt = value; }
    // True once the population baseline has been read from spieler.php this session. Re-armed (set
    // false) on a real village switch so the next active village can seed its own baseline.
    private bool _populationBaselineRead { get => _session.PopulationBaselineRead; set => _session.PopulationBaselineRead = value; }
    // Backed by the shared session cache so the logged-in throttle survives across per-operation clients.
    private DateTimeOffset _lastEnsureLoggedInAt { get => _session.LastEnsureLoggedInAt; set => _session.LastEnsureLoggedInAt = value; }
    private DateTimeOffset _lastUiSyncAt = DateTimeOffset.MinValue;
    private bool _lastEnsureLoggedInSucceeded { get => _session.LastEnsureLoggedInSucceeded; set => _session.LastEnsureLoggedInSucceeded = value; }
    private int _suppressEnsureUiSyncDepth;
    private static readonly object NatarCacheSync = new();
    private static readonly Dictionary<string, List<NatarCoordinateJs>> CachedNatarCoordinatesByAccount = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CachedVillageResourceSnapshot
    {
        public IReadOnlyDictionary<string, double?> ProductionByHour { get; init; } = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ResourceField> ResourceFields { get; init; } = [];
        public long? WarehouseCapacity { get; init; }
        public long? GranaryCapacity { get; init; }
    }

    public TravianClient(
        IPage page,
        BotOptions config,
        AccountOptions account,
        bool interactive = true,
        bool browserVisible = true,
        string? projectRoot = null,
        ICaptchaAutoSolver? captchaAutoSolver = null,
        Action<string>? statusCallback = null,
        TravianSessionCache? sessionCache = null)
    {
        _page = page;
        _config = config;
        _account = account;
        _interactive = interactive;
        _browserVisible = browserVisible;
        // Shared across per-operation clients for the same browser session; falls back to a fresh
        // private cache when none is supplied (e.g. in tests).
        _session = sessionCache ?? new TravianSessionCache();
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? Directory.GetCurrentDirectory()
            : projectRoot;
        _capitalCachePath = AccountStoragePaths.CapitalStatePath(_projectRoot, _account.Name);
        _heroAttributeSnapshotStore = new HeroAttributeSnapshotStore(_projectRoot);
        _natarFarmCacheStore = new NatarFarmCacheStore(_projectRoot);
        _captchaAutoSolver = captchaAutoSolver;
        _statusCallback = statusCallback;
    }

    public string AccountName => _account.Name;
    public string ServerUrl => _config.BaseUrl.TrimEnd('/');
    public string? KnownTribe => IsKnownTribe(_sessionTribe) ? _sessionTribe : IsKnownTribe(_cachedTribe) ? _cachedTribe : null;
    public bool? KnownGoldClubEnabled => _cachedGoldClubEnabled;
    
// Helper för action pacing klick
    private Task DelayBeforeClickAsync(
        CancellationToken cancellationToken,
        string? reason = null)
    {
        if (!_config.ActionPacingEnabled)
        {
            return Task.CompletedTask;
        }

        return ActionPacer.FromOptions(_config, Notify).DelayAsync(
            _config.ActionPacingClickMinSeconds,
            _config.ActionPacingClickMaxSeconds,
            cancellationToken,
            reason);
    }

    private Task DelayFarmListStepAsync(CancellationToken cancellationToken)
    {
        return new ActionPacer(enabled: true, Notify).DelayAsync(
            _config.FarmListStepDelayMinSeconds,
            _config.FarmListStepDelayMaxSeconds,
            cancellationToken);
    }

// Helper function for waiting on a page to fully load with retries, to mitigate transient timeouts on slow-loading pages.
    private async Task WaitForPageReadyAsync(CancellationToken cancellationToken = default)
    {
        const int attempts = 3;
        const int timeoutMs = 10000;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await PauseForManualStepIfVisibleAsync("Manual verification appeared on the page.", cancellationToken);
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = timeoutMs,
                }).WaitAsync(cancellationToken);

                await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
                {
                    Timeout = timeoutMs,
                }).WaitAsync(cancellationToken);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PlaywrightException ex)
            {
                if (attempt < attempts)
                    Notify($"[WaitForPageReadyAsync] Page did not load, retry {attempt + 1}/{attempts}. Timeout: {timeoutMs} ms. Url='{_page.Url}'. {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                if (attempt < attempts)
                    Notify($"[WaitForPageReadyAsync] Page did not load, retry {attempt + 1}/{attempts}. Timeout: {timeoutMs} ms. Url='{_page.Url}'. {ex.Message}");
            }
        }

        Notify($"[WaitForPageReadyAsync] Page did not load after {attempts} attempts. Url='{_page.Url}'.");
    }

    // Reloads whatever page the browser is currently on to keep a long-idle session fresh. This avoids
    // Travian's own "auto-reload failed" stale state (a countdown that expired without reloading), which
    // makes the page show wrong/old values. Re-verifies login after the reload.
    public async Task RefreshCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        Notify($"[keep-alive] refreshing current page to avoid a stale session. Url='{_page.Url}'");
        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
            .WaitAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await EnsureLoggedInAsync();
    }

    // Login function
    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        Notify("[LoginAsync started]");
        Notify($"[login] Account='{_account.Name}' server='{ServerUrl}' — starting");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before login.", cancellationToken);
        var state = await LoginStateAsync();
        if (state == "logged_in")
        {
            Notify($"[login] already logged in as '{_account.Name}'");
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }

        if (state == "unknown" && IsLikelyGamePageUrl(_page.Url))
        {
            Notify("[login] state unknown on game page; rechecking dorf1 before opening login page.");
            await GotoAsync(_config.VillageOverviewPath, cancellationToken);
            if (await IsLoggedInAsync())
            {
                Notify($"[login] already logged in as '{_account.Name}' after dorf1 recheck");
                await RefreshAccountFeatureSignalsAsync(cancellationToken);
                return;
            }
        }


        var loggedInFromCurrentPage = await TryLoginUsingCurrentPageAsync(cancellationToken);
        if (loggedInFromCurrentPage)
        {
            Notify($"[login] success ({_account.Name}) — used existing page form");
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }

        await GotoAsync(_config.LoginPath, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on the login page.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            Notify($"[login] success ({_account.Name}) — already authenticated after opening login page");
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
            return;
        }

        // Tolerate a slow-loading login page: wait for the form to appear before filling it.
        if (!await WaitForAnySelectorAsync(Selectors.LoginUsernameField, TimeSpan.FromSeconds(15), cancellationToken))
        {
            // Maybe a redirect logged us in, or a captcha/manual step is blocking the form.
            if (await IsLoggedInAsync())
            {
                await RefreshAccountFeatureSignalsAsync(cancellationToken);
                return;
            }

            if (!await CaptchaOrManualStepVisibleAsync())
            {
                throw new InvalidOperationException("Login form did not load (the page may be slow or unavailable).");
            }
        }

        await FillLoginCredentialsWithPacingAsync(cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            var screenshotPath = await CaptureManualVerificationScreenshotAsync("login-page", cancellationToken);
            Notify($"[login] captcha/manual step detected for {_account.Name} — attempting auto-solve");
            if (await TrySolveCaptchaAutomaticallyAsync("login-page", screenshotPath, cancellationToken))
            {
                var loggedInAfterAutoSolve = await WaitUntilLoggedInAsync(cancellationToken);
                if (loggedInAfterAutoSolve)
                {
                    Notify($"[login] success ({_account.Name}) — captcha auto-solve cleared the block");
                    await RefreshAccountFeatureSignalsAsync(cancellationToken);
                    return;
                }
            }

            if (!_browserVisible)
            {
                throw new ManualVerificationRequiredException(
                    "Captcha/manual verification appeared while running headless.");
            }

            Notify($"[login] ALARM: captcha auto-solve failed for {_account.Name}. Solve it manually in the browser — bot is paused.");
            await WaitForManualVerificationToClearAsync(cancellationToken);
        }
        else
        {
            await ClickLoginButtonAsync(cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        }

        var loggedIn = await WaitUntilLoggedInAsync(cancellationToken);
        if (!loggedIn)
        {
            throw new InvalidOperationException("Login did not complete successfully.");
        }
        // Wait for the post-login landing page (dorf1) to fully render before any task navigates
        // away. DOMContentLoaded alone fires before stylesheets/scripts finish, which made the bot
        // switch pages on a half-loaded page and also produced transient 'unknown' login-state reads
        // on the next tick. Wait for the full Load state, then apply the configurable page-load pace.
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = _config.TimeoutMs,
            });
            await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
            {
                Timeout = _config.TimeoutMs,
            });
            Notify("[login] page successfully loaded.");
            await ActionPacer.FromOptions(_config, Notify).DelayAsync(
                _config.ActionPacingPageLoadMinSeconds,
                _config.ActionPacingPageLoadMaxSeconds,
                cancellationToken,
                "login: after page load");
        }
        catch
        {
            Notify("[login] Warning: timeout waiting for page after login.");
        }
        Notify($"[login] success ({_account.Name}) — submitted credentials and confirmed");
        await RefreshAccountFeatureSignalsAsync(cancellationToken);
    }

    public async Task RefreshAccountFeatureSignalsAsync(CancellationToken cancellationToken = default)
    {
        // Plus can change during a round. Tribe cannot, and Gold Club only matters as a latched true.
        if (_cachedTravianPlusActive.HasValue
            && (_cachedGoldClubEnabled == true)
            && IsKnownTribe(_sessionTribe)
            && DateTimeOffset.UtcNow - _cachedTribePlusAt < TimeSpan.FromSeconds(60))
        {
            return;
        }

        try
        {
            var plus = await IsTravianPlusActiveAsync(cancellationToken);
            if (!_cachedTravianPlusActive.HasValue || _cachedTravianPlusActive != plus)
            {
                _cachedTravianPlusActive = plus;
                Notify($"[plus] active={plus}");
            }
            _cachedTribePlusAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Notify($"Plus status check failed: {ex.Message}");
        }

        // Gold Club is monotonic — once true within a session it cannot revert. Skip re-checks once latched.
        if (_cachedGoldClubEnabled != true)
        {
            try
            {
                var gold = await ReadGoldClubEnabledAsync(cancellationToken);
                if (!_cachedGoldClubEnabled.HasValue || _cachedGoldClubEnabled != gold)
                {
                    Notify($"[goldclub] active={gold}");
                }
                _cachedGoldClubEnabled = gold;
            }
            catch (Exception ex)
            {
                Notify($"Gold Club status check failed: {ex.Message}");
            }
        }

        if (!IsKnownTribe(_sessionTribe))
        {
            try
            {
                var tribe = await ReadTribeAsync(cancellationToken);
                if (IsKnownTribe(tribe))
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

        Notify($"[login] form already on current page — submitting inline for {_account.Name}");

        await FillLoginCredentialsWithPacingAsync(cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            var screenshotPath = await CaptureManualVerificationScreenshotAsync("login-current-page", cancellationToken);
            Notify($"[login] captcha/manual step on inline form for {_account.Name} — attempting auto-solve");
            if (await TrySolveCaptchaAutomaticallyAsync("login-current-page", screenshotPath, cancellationToken))
            {
                var autoSolvedLoggedIn = await WaitUntilLoggedInAsync(cancellationToken);
                if (autoSolvedLoggedIn)
                {
                    Notify($"[login] success ({_account.Name}) — inline form, captcha auto-solved");
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
            Notify($"[login] success ({_account.Name}) — inline form submitted");
        }

        return loggedIn;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        Notify($"[logout] account='{_account.Name}' — starting");
        _sessionTribe = null;
        _cachedTribe = null;
        _cachedGoldClubEnabled = null;
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before logout.", cancellationToken);
        if (!await IsLoggedInAsync())
        {
            Notify($"[logout] {_account.Name} was already logged out");
            return;
        }

        var clicked = await TryTriggerLogoutAsync(cancellationToken);
        if (clicked)
        {
            // Official T4.6 logout is an AJAX call (Travian.api('auth/logout')) that then redirects to
            // the login scene. Wait for a positive logged-out marker (login form) before declaring success.
            Notify("[logout] clicked logout control — waiting for the login page to confirm sign-out.");
            if (await WaitForLoggedOutAsync(cancellationToken))
            {
                Notify($"[logout] {_account.Name} logged out successfully");
                return;
            }

            Notify("[logout] click did not confirm sign-out — trying legacy logout URLs.");
        }

        // Fallback for servers where the logout control is missing or the click did not take effect.
        foreach (var candidatePath in Paths.LogoutCandidates)
        {
            await GotoAsync(candidatePath, cancellationToken);
            if (await WaitForLoggedOutAsync(cancellationToken))
            {
                Notify($"[logout] {_account.Name} logged out via navigation to {candidatePath}");
                return;
            }
        }

        throw new InvalidOperationException("Logout did not complete successfully.");
    }

    // Triggers the logout control. The official T4.6 control is an <a> with only an onclick
    // (Travian.api('auth/logout')) and is often hidden behind a menu, so a normal actionability-gated
    // click times out. Dispatching the click event runs the element's own handler/navigation directly,
    // without waiting for visibility/stability — which also works for the SS/legacy href-based links.
    private async Task<bool> TryTriggerLogoutAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in Selectors.LogoutTriggers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _page.Locator(selector).CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await _page.DispatchEventAsync(selector, "click");
                Notify($"[logout] triggered logout via '{selector}'.");
                return true;
            }
            catch (PlaywrightException ex)
            {
                Notify($"[logout] dispatch on '{selector}' failed: {ex.Message}");
            }
        }

        return false;
    }

    // Confirms sign-out by waiting for a positive logged-out marker (login form / login scene), not just
    // the absence of logged-in markers — a page that is still rendering would otherwise read as a false
    // logout. Returns true as soon as the page state is confirmed "logged_out".
    private async Task<bool> WaitForLoggedOutAsync(CancellationToken cancellationToken)
    {
        const int attempts = 10;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((await LoginStateAsync()) == "logged_out")
            {
                return true;
            }

            await Task.Delay(500, cancellationToken);
        }

        Notify("[logout] sign-out not confirmed yet (no login page detected).");
        return false;
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

    public async Task<VillageStatus> ReadVillageStatusAsync(
        IReadOnlyList<Village> knownVillages,
        IReadOnlyList<Building> knownBuildings,
        CancellationToken cancellationToken = default)
    {
        Notify("ReadVillageStatusAsync started with known villages/buildings");
        if (!IsCurrentUrlForPath(_config.VillageOverviewPath))
        {
            await GotoAsync(_config.VillageOverviewPath, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the village overview.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        return await ReadCurrentVillageStatusAsync(cancellationToken, knownVillages, knownBuildings);
    }

    public async Task<bool> CheckLoggedInAsync(CancellationToken cancellationToken = default)
    {
        Notify("CheckLoggedInAsync started");
        cancellationToken.ThrowIfCancellationRequested();
        return await IsLoggedInAsync();
    }

    public async Task<AccountSnapshot> ReadAccountSnapshotAsync(
        bool forceRefreshVillages = false,
        bool preferCurrentPageVillages = false,
        bool restorePageAfterProfile = true,
        bool suppressEnsureUiSync = false,
        bool skipOverviewNavigation = false,
        CancellationToken cancellationToken = default)
    {
        Notify("ReadAccountSnapshotAsync started");
        if (suppressEnsureUiSync)
        {
            _suppressEnsureUiSyncDepth++;
        }

        try
        {
            // Normally we land on the village overview first for a stable page state. The post-login
            // flow passes skipOverviewNavigation=true when it just read the hero inventory and is
            // about to refresh villages from the profile anyway — that saves one dorf1 hop, since
            // ReadVillagesFromServerAsync navigates straight to the profile regardless.
            if (!skipOverviewNavigation && !IsCurrentUrlForPath(_config.VillageOverviewPath))
            {
                await GotoAsync(_config.VillageOverviewPath, cancellationToken);
            }

            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading account info.", cancellationToken);
            await EnsureLoggedInAsync();

            IReadOnlyList<Village> villages;
            if (forceRefreshVillages)
            {
                villages = await ReadVillagesFromServerAsync(cancellationToken, restorePreviousUrl: restorePageAfterProfile);
                if (villages.Count > 0)
                {
                    _cachedVillages = villages.ToList();
                    _cachedVillagesAt = DateTimeOffset.UtcNow;
                    if (villages.Any(v => v.Population.HasValue))
                    {
                        _cachedVillagesPopulationAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            else
            {
                villages = preferCurrentPageVillages
                    ? await ReadVillagesPreferCacheAsync(cancellationToken)
                    : await ReadVillagesAsync(cancellationToken);
            }

            return new AccountSnapshot(
                Tribe: await ReadTribeAsync(cancellationToken),
                ActiveVillage: await ReadActiveVillageNameAsync(cancellationToken),
                VillageCount: villages.Count,
                Villages: villages,
                ServerTimeUtc: _serverTimeUtc);
        }
        finally
        {
            if (suppressEnsureUiSync)
            {
                _suppressEnsureUiSyncDepth--;
            }
        }
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

        // The farm list page is React-rendered, so the wrappers can be missing on the first read
        // right after navigation (a known race that "worked on retry"). Wait for them to render and,
        // on Official, retry the whole open/expand/read once if the page still yielded zero lists.
        const int maxAttempts = 2;
        IReadOnlyList<FarmListOverview> rows = [];
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            await WaitForFarmListsRenderedAsync(cancellationToken);
            await EnsureOfficialFarmListsExpandedAsync(cancellationToken);
            rows = await ReadFarmListsFromCurrentPageAsync(cancellationToken);
            if (rows.Count > 0 || _config.IsPrivateServer || attempt == maxAttempts)
            {
                break;
            }

            Notify($"[farm-list] no farm lists read on attempt {attempt}/{maxAttempts}; reopening the farm page and retrying.");
            await Task.Delay(750, cancellationToken);
        }

        Notify($"[farm-list] read {rows.Count} farm list(s) from rally point");
        return rows;
    }

    // Waits for the Official farm list wrappers to render before reading, so a slow React mount does
    // not make us read an empty page. A genuinely empty account simply times out and reads zero.
    private async Task WaitForFarmListsRenderedAsync(CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            return;
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => document.querySelectorAll('#rallyPointFarmList .farmListWrapper').length > 0",
                null,
                new PageWaitForFunctionOptions { Timeout = 8000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] no farm list wrappers rendered within 8 seconds; the account may have no farm lists.");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] waiting for farm list wrappers failed: {ex.Message}");
        }
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
        Notify($"[farm-list] '{farmListName}' sent — next ready in {(remaining is > 0 ? TravianParsing.FormatDuration(remaining.Value) : "now")}");
        return remaining;
    }

    public async Task<int> SendAllFarmListsNowAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await WaitForDispatchLimitToClearAsync(cancellationToken);

        var clickState = await TryClickStartAllFarmListsAsync(cancellationToken);
        if (clickState.ListCount <= 0)
        {
            throw new InvalidOperationException("No farm lists were found for start-all farming.");
        }

        if (!clickState.Clicked)
        {
            throw new InvalidOperationException($"Could not click Travian Start all farmlists button: {clickState.Reason ?? "unknown reason"}.");
        }

        Notify($"[farm-list] send-all started for {clickState.ListCount} list(s).");
        await WaitForFarmListStartButtonsDisabledAsync(clickState.ListIds ?? [], cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting all farmlists.", cancellationToken);
        await TryClickCaptchaSuccessDialogOkAsync(cancellationToken);
        await WaitForFarmListStartButtonsEnabledAsync(clickState.ListIds ?? [], cancellationToken);
        Notify($"[farm-list] send-all completed for {clickState.ListCount} list(s).");
        return clickState.ListCount;
    }

    public async Task<FarmListLossDeactivationResult> DeactivateFarmListLossTargetsAsync(
        bool includeUnoccupiedOasis,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        if (!await ReadGoldClubEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Gold Club is not enabled for this account.");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await WaitForFarmListsRenderedAsync(cancellationToken);
        await EnsureOfficialFarmListsExpandedAsync(cancellationToken);

        var initialRows = await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
        var lossRows = initialRows.Where(IsFarmListLossRow).ToList();
        var skippedOasisRows = lossRows.Count(row =>
            !includeUnoccupiedOasis && FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName));
        var unknownRaidClasses = initialRows
            .Where(row => !string.IsNullOrWhiteSpace(row.RaidClass))
            .Where(row => row.RaidClass!.Contains("attack_", StringComparison.OrdinalIgnoreCase))
            .Where(row => FarmListLossStateClassifier.Classify(row.RaidClass) == FarmListLossState.Unknown)
            .Select(row => row.RaidClass!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        if (unknownRaidClasses.Count > 0)
        {
            Notify($"[farm-list] red/yellow scan saw unknown raid state class(es): {string.Join(", ", unknownRaidClasses)}");
        }

        Notify($"[farm-list] red/yellow loss scan found {lossRows.Count} active row(s); skipped oasis={skippedOasisRows}; includeOasis={includeUnoccupiedOasis}.");

        var deactivated = 0;
        for (var attempt = 1; attempt <= MaxFarmsPerFarmList * 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = attempt == 1
                ? initialRows
                : await ReadFarmListLossRowsFromCurrentPageAsync(cancellationToken);
            var candidate = rows.FirstOrDefault(row => IsFarmListLossDeactivationCandidate(row, includeUnoccupiedOasis));
            if (candidate is null)
            {
                break;
            }

            var clicked = await TryDeactivateFarmListLossRowAsync(candidate, cancellationToken);
            if (!clicked)
            {
                Notify($"[farm-list] could not deactivate loss row target='{candidate.TargetName}' slot='{candidate.SlotId}' list='{candidate.ListName}'.");
                break;
            }

            deactivated++;
            Notify($"[farm-list] deactivated loss row target='{candidate.TargetName}' slot='{candidate.SlotId}' list='{candidate.ListName}' state='{candidate.RaidClass}'.");
            await Task.Delay(300, cancellationToken);
        }

        Notify($"[farm-list] red/yellow loss deactivation done: found={lossRows.Count}, deactivated={deactivated}, skippedOasis={skippedOasisRows}.");
        return new FarmListLossDeactivationResult(lossRows.Count, deactivated, skippedOasisRows);
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
            lid,
            false,
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
        IProgress<int>? addedProgress = null,
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

        var coordinates = await ReadNatarFarmCoordinatesCachedAsync(cancellationToken);
        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("Could not read any 'Natar farm village' coordinates from Natars profile.");
        }

        return await AddFarmsFromCoordinatesCoreAsync(
            farmListName,
            troopType,
            troopCount,
            requestedCount,
            coordinates
                .Where(coordinate => coordinate.X.HasValue && coordinate.Y.HasValue)
                .Select(coordinate => new FarmCoordinate(coordinate.X!.Value, coordinate.Y!.Value))
                .ToList(),
            addedProgress,
            null,
            false,
            cancellationToken);
    }

    public async Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops = false,
        IProgress<FarmAddProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (_config.IsPrivateServer)
        {
            throw new InvalidOperationException("Travco farm lists are only available on official servers.");
        }

        if (string.IsNullOrWhiteSpace(farmListName))
        {
            throw new InvalidOperationException("Farm list name is required.");
        }

        if (!useDefaultTroops && string.IsNullOrWhiteSpace(troopType))
        {
            throw new InvalidOperationException("Troop type is required.");
        }

        if (!useDefaultTroops && troopCount <= 0)
        {
            throw new InvalidOperationException("Troop count must be greater than 0.");
        }

        if (coordinates.Count <= 0)
        {
            throw new InvalidOperationException("At least one farm coordinate is required.");
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

        return await AddFarmsFromCoordinatesCoreAsync(
            farmListName,
            troopType,
            troopCount,
            requestedCount,
            coordinates,
            null,
            progress,
            useDefaultTroops,
            cancellationToken);
    }

    private async Task<FarmAddBatchResult> AddFarmsFromCoordinatesCoreAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        IProgress<int>? addedProgress,
        IProgress<FarmAddProgress>? progress,
        bool useDefaultTroops,
        CancellationToken cancellationToken)
    {
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var lid = await TryResolveFarmListSlotIdByNameAsync(farmListName, cancellationToken);
        if (string.IsNullOrWhiteSpace(lid))
        {
            throw new InvalidOperationException($"Could not find farm list '{farmListName}' on farm page.");
        }

        var capacityLimit = requestedCount;
        if (!_config.IsPrivateServer)
        {
            var currentFarmCount = await ReadOfficialFarmListFarmCountAsync(lid, cancellationToken);
            if (!currentFarmCount.HasValue)
            {
                throw new InvalidOperationException($"Could not read the current farm count for '{farmListName}'.");
            }

            capacityLimit = Math.Max(0, OfficialFarmListCapacity - currentFarmCount.Value);
            if (capacityLimit <= 0)
            {
                throw new InvalidOperationException($"Farm list '{farmListName}' is full (100/100).");
            }
        }

        var targetAddedCount = Math.Min(requestedCount, capacityLimit);
        Notify($"Starting add farms batch: target={targetAddedCount}, available={coordinates.Count}.");
        var added = 0;
        var alreadyInList = 0;
        var failed = 0;
        var notFound = 0;
        var attempted = 0;
        var invalidCoordinates = new List<FarmCoordinate>();
        for (var i = 0; i < coordinates.Count && added < targetAddedCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_config.IsPrivateServer)
            {
                var currentFarmCount = await ReadOfficialFarmListFarmCountAsync(lid, cancellationToken);
                if (!currentFarmCount.HasValue)
                {
                    throw new InvalidOperationException($"Could not verify the current farm count for '{farmListName}'.");
                }

                if (currentFarmCount.Value >= OfficialFarmListCapacity)
                {
                    Notify($"[farm-list] '{farmListName}' reached 100/100; stopping before another Add target attempt.");
                    break;
                }
            }

            var coordinate = coordinates[i];
            attempted++;
            var stepPrefix = $"[checked={attempted}, added={added}/{targetAddedCount}]";

            await OpenAddRaidFormAsync(lid, cancellationToken);

            var saveOutcome = await TryFillAddRaidFormAndSaveAsync(
                farmListName,
                troopType.Trim(),
                troopCount,
                coordinate.X,
                coordinate.Y,
                lid,
                useDefaultTroops,
                cancellationToken);

            if (saveOutcome == AddRaidSaveOutcome.Added)
            {
                added++;
                addedProgress?.Report(added);
                Notify($"{stepPrefix} Added farm ({coordinate.X}|{coordinate.Y}) to '{farmListName}'.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.AlreadyInList)
            {
                alreadyInList++;
                Notify($"{stepPrefix} Farm ({coordinate.X}|{coordinate.Y}) is already in '{farmListName}' (This village is already in the selected farm list.).");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
                continue;
            }

            if (saveOutcome == AddRaidSaveOutcome.InvalidCoordinates)
            {
                failed++;
                notFound++;
                invalidCoordinates.Add(coordinate);
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}): there is no village at these coordinates.");
                progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
                continue;
            }

            failed++;
            Notify($"{stepPrefix} Failed to save farm ({coordinate.X}|{coordinate.Y}) in '{farmListName}'.");
            progress?.Report(new FarmAddProgress(farmListName, attempted, targetAddedCount, added, notFound));
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        return new FarmAddBatchResult(
            farmListName,
            requestedCount,
            attempted,
            added,
            alreadyInList,
            failed,
            notFound,
            invalidCoordinates);
    }

    private async Task<int?> ReadOfficialFarmListFarmCountAsync(string lid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<int?>(
            """
            (listId) => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(listId));
              const match = clean(wrapper?.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
              return match ? Number(match[1]) : null;
            }
            """,
            lid);
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
        long troopCount,
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
            var stepPrefix = $"[farm-manual] [{attempted}/{coordinates.Count}]";
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

                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                
                continue;
            }

            if (sendResult.Status == ManualAttackSendStatus.SkippedLowTroops)
            {
                skipped++;
                consecutiveLowTroopSkips++;
                Notify($"{stepPrefix} Skipped ({coordinate.X}|{coordinate.Y}). Available {troopType.Trim()}: {FormatLargeCount(sendResult.AvailableTroopCount)}, required minimum: {FormatLargeCount(sendResult.MinimumAcceptedTroopCount)}.");
                if (consecutiveLowTroopSkips >= ManualFarmingMaxConsecutiveLowTroopSkips)
                {
                    Notify($"[farm-manual] stopping — {consecutiveLowTroopSkips} consecutive low-troop skips reached");
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
        Notify("[scan] all-village status scan starting");
        var returnVillageName = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the village overview.", cancellationToken);
        await EnsureLoggedInAsync();

        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            return [await ReadCurrentVillageStatusAsync(cancellationToken)];
        }

        var statuses = new List<VillageStatus>();
        try
        {
            var scanIndex = 0;
            foreach (var village in villages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanIndex++;
                Notify($"[scan:verbose] reading village '{village.Name}' ({scanIndex}/{villages.Count})");

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
        }
        finally
        {
            Notify($"[scan] all-village status scan finished — read {statuses.Count}/{villages.Count} village(s)");
            if (!string.IsNullOrWhiteSpace(returnVillageName))
            {
                try
                {
                    await SwitchToVillageAsync(returnVillageName, cancellationToken: CancellationToken.None, skipFeatureRefresh: true);
                    await GotoAsync(Paths.Resources, CancellationToken.None);
                    Notify($"[scan:verbose] returned to original village '{returnVillageName}' on dorf1");
                }
                catch (Exception ex)
                {
                    Notify($"[scan] could not return to original village '{returnVillageName}': {ex.Message}");
                }
            }
        }

        return statuses;
    }

    public async Task SwitchToVillageAsync(string villageName = "", string? villageUrl = null, CancellationToken cancellationToken = default, bool skipFeatureRefresh = false)
    {
        var activeVillageBeforeSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        var requestedLabel = !string.IsNullOrWhiteSpace(villageName)
            ? $"'{villageName}'"
            : (!string.IsNullOrWhiteSpace(villageUrl) ? $"url={villageUrl}" : "(unspecified)");
        Notify($"[village-switch] requested {requestedLabel} — current='{activeVillageBeforeSwitch ?? "(unknown)"}'");

        // If we are already on the requested village, no navigation is needed.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !string.IsNullOrWhiteSpace(activeVillageBeforeSwitch)
            && IsSameVillageName(activeVillageBeforeSwitch, villageName))
        {
            Notify($"[village-switch] already on '{villageName}' — no navigation needed");
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
        // (`<a class="village-name" href="dorf1.php?newdid=X">`). It's present on every page and always
        // carries the correct newdid. Prefer it over a passed-in/cached URL whenever the village name is
        // known — cached payload URLs can be stale or wrong (wrong newdid silently fails to switch and
        // desyncs the working village). Fall back to the passed-in URL only if the sidebar has no match.
        if (!string.IsNullOrWhiteSpace(villageName))
        {
            var sidebarUrl = await TryGetVillageHrefFromSidebarAsync(villageName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(sidebarUrl))
            {
                url = sidebarUrl;
            }
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            await GotoAsync(TravianUrls.CanonicalizeVillageSwitchUrl(url), cancellationToken);
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

            await GotoAsync(TravianUrls.CanonicalizeVillageSwitchUrl(match.Url!), cancellationToken);
        }
        else
        {
            return;
        }

        // A village switch must land on a logged-in game page. A contaminated/stale switch URL can hit
        // the site root, which the server serves as the login page (looks like a logout). Detect that
        // here and recover via the normal login flow instead of silently reading the wrong village.
        if ((await LoginStateAsync()) != "logged_in")
        {
            Notify($"[village-switch] landed on a non-game page after switching to {requestedLabel} — recovering via login.");
            await EnsureLoggedInAsync(force: true, cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while switching to village '{villageName}'.", cancellationToken);
        await EnsureLoggedInAsync();
        var activeVillageAfterSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);

        // Verify we actually landed on the REQUESTED village — not merely that "something changed". A stale
        // or wrong newdid in the payload URL navigates but leaves the active village unchanged, which would
        // desync the program's working village from the browser (it would then run tasks on the wrong
        // village). When a name was requested and we are not on it, retry once via the in-page sidebar href,
        // which always carries the correct newdid, then re-verify.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !IsSameVillageName(activeVillageAfterSwitch, villageName))
        {
            Notify($"[village-switch] expected '{villageName}' but active village reads '{activeVillageAfterSwitch ?? "(unknown)"}' — retrying via sidebar.");
            var sidebarUrl = await TryGetVillageHrefFromSidebarAsync(villageName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(sidebarUrl))
            {
                var canonicalSidebar = TravianUrls.CanonicalizeVillageSwitchUrl(sidebarUrl);
                var canonicalUsed = string.IsNullOrWhiteSpace(url) ? string.Empty : TravianUrls.CanonicalizeVillageSwitchUrl(url);
                if (!string.Equals(canonicalSidebar, canonicalUsed, StringComparison.OrdinalIgnoreCase))
                {
                    await GotoAsync(canonicalSidebar, cancellationToken);
                    await EnsureLoggedInAsync();
                    activeVillageAfterSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);
                }
            }
        }

        // Still not on the requested village after the sidebar retry: abort rather than run the task on the
        // wrong village. The caller (runtime loop) will retry on the next cycle.
        if (!string.IsNullOrWhiteSpace(villageName)
            && !IsSameVillageName(activeVillageAfterSwitch, villageName))
        {
            Notify(
                $"[village-switch] village_missing_suspected requested='{villageName}' " +
                $"active='{activeVillageAfterSwitch ?? "(unknown)"}'; aborting before task execution.");
            throw new InvalidOperationException(
                $"Village switch failed: requested '{villageName}' but the browser is on '{activeVillageAfterSwitch ?? "(unknown)"}'. "
                + "Aborting so the task does not run on the wrong village.");
        }

        if (!IsSameVillageName(activeVillageBeforeSwitch, activeVillageAfterSwitch))
        {
            Notify($"[village-switch] now on '{activeVillageAfterSwitch ?? "(unknown)"}' (was '{activeVillageBeforeSwitch ?? "(unknown)"}')");
            // Allow population to refresh from the sidebar on the next read.
            _cachedVillagesPopulationAt = DateTimeOffset.MinValue;
            _populationBaselineRead = false;
            // NOTE: no capital re-check here. RefreshCapitalStateForActiveVillageAsync navigates to
            // spieler.php (profile), which added a slow extra navigation on every switch and caused the
            // resource read to occasionally land mid-navigation (resources/storage not filling). Capital
            // is resolved from cache + fast-detection (resource field > level 10) on the resource read,
            // and at login analysis — so the switch only needs dorf1/dorf2 of the target village.
        }
        else if (!string.IsNullOrWhiteSpace(villageName))
        {
            // Same name before and after, but it IS the requested village (we were already there or the
            // sidebar retry confirmed it) — nothing to log as a change.
        }
        else
        {
            Notify($"[village-switch] navigation completed but active village still reads '{activeVillageAfterSwitch ?? "(unknown)"}'");
        }

        if (!skipFeatureRefresh)
        {
            // Re-emit account signals so UI refreshes after a village switch (Plus/Gold can be unchanged but UI may not have them yet).
            await RefreshAccountFeatureSignalsAsync(cancellationToken);
        }
    }

    private static bool IsSameVillageName(string? left, string? right)
    {
        var normalizedLeft = NormalizeVillageNameForComparison(left);
        var normalizedRight = NormalizeVillageNameForComparison(right);
        return normalizedLeft.Length > 0
            && normalizedRight.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVillageNameForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value
            .Replace("\u202A", string.Empty)
            .Replace("\u202B", string.Empty)
            .Replace("\u202C", string.Empty)
            .Replace("\u202D", string.Empty)
            .Replace("\u202E", string.Empty)
            .Replace("\u200E", string.Empty)
            .Replace("\u200F", string.Empty)
            .Replace('−', '-');
        cleaned = Regex.Replace(cleaned, @"\s*\(\s*-?\d+\s*\|\s*-?\d+\s*\)\s*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private async Task<string?> TryGetVillageHrefFromSidebarAsync(string villageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Notify($"[village-switch:verbose] sidebar lookup for '{villageName}'");
        try
        {
            var href = await _page.EvaluateAsync<string?>(
                """
                (name) => {
                  const norm = (t) => (t || '')
                    .replace(/[‪-‮‎‏]/g, '')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                  const wanted = norm(name);
                  if (!wanted) return null;

                  // Official T4.6: the village switcher is React-rendered with no plain newdid anchors —
                  // each entry instead carries the real switch id in data-did and the clean name in a child
                  // ".name". Match on the exact name (short names like "BI"/"PI" must not substring-match)
                  // and build the canonical switch URL from data-did.
                  for (const entry of document.querySelectorAll('.listEntry.village[data-did]')) {
                    const did = entry.getAttribute('data-did');
                    if (!did) continue;
                    const nameEl = entry.querySelector('.name');
                    const text = norm(nameEl ? nameEl.textContent : entry.textContent);
                    if (text === wanted) {
                      return '/dorf1.php?newdid=' + did;
                    }
                  }

                  // Legacy/SS or other layouts: static anchors carrying newdid in the href.
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
                    const text = norm(link.textContent);
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

            if (string.IsNullOrWhiteSpace(href))
            {
                Notify($"[village-switch:verbose] sidebar had no matching link for '{villageName}' — falling back to spieler.php village list");
                return null;
            }
            var resolved = ResolveUrl(href);
            Notify($"[village-switch:verbose] sidebar matched '{villageName}' → {resolved}");
            return resolved;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[village-switch:verbose] sidebar lookup transient navigation for '{villageName}': {ex.Message}");
            return null;
        }
    }

    private async Task<VillageStatus> ReadCurrentVillageStatusAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<Village>? knownVillages = null,
        IReadOnlyList<Building>? knownBuildings = null)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading village status.", cancellationToken);
        // Read the village list from the sidebar/cache instead of navigating to the profile (spieler.php).
        // On a village switch we only need dorf1/dorf2 of the target village for status; the profile was
        // only used to enumerate villages and re-check the capital — capital comes from cache here. This
        // avoids the extra (slow) profile navigation on every switch/status read.
        var villages = knownVillages is { Count: > 0 }
            ? knownVillages
            : await ReadVillagesPreferCacheAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        var currency = await ReadCurrencyAsync(cancellationToken);
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        var snapshot = await ReadResourceSnapshotAsync(cancellationToken);
        var resources = snapshot.Resources;
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = BuildResourceForecasts(resources, capacities, productionByHour);
        var usingCachedProduction = !HasAnyProduction(snapshot.ProductionByHour) && HasAnyProduction(cachedSnapshot?.ProductionByHour);
        Notify($"Resource read: storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}{(usingCachedProduction ? " (cached production)" : string.Empty)}");

        var resourceFields = await ReadResourceFieldsAsync(cancellationToken);

        // Persist the per-village resource snapshot here too. This full status read runs on dorf1 right
        // after a village switch and is often the only place production is read for a freshly-switched
        // village. Without saving it, later current-page reads (on dorf2/build pages, where production is
        // not present) found an empty cache and showed "@-/h"/"not filling". SaveCached keeps existing
        // values when the new read is empty, so it never overwrites good data with blanks.
        SaveCachedVillageResourceSnapshot(activeVillage, resourceFields, capacities, productionByHour);

        var buildings = knownBuildings is { Count: > 0 }
            ? knownBuildings
            : await ReadBuildingsAsync(cancellationToken);
        // Read Travian's own in-progress construction list so the UI can show upgrades that were
        // started outside the program (e.g. manually before login) with the target level in
        // parentheses. We are on dorf1/dorf2 after the reads above, both of which carry the list,
        // so no extra navigation is needed.
        var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings: false);
        var heroStatus = await ReadHeroStatusAsync(cancellationToken);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: villages,
            Resources: resources,
            ResourceFields: resourceFields,
            Buildings: buildings,
            BuildQueue: buildQueue,
            Tribe: await ReadTribeAsync(cancellationToken),
            VillageCount: villages.Count,
            Gold: currency.Gold,
            Silver: currency.Silver,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc,
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            HeroStatus: heroStatus,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview);
    }

    private async Task GotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{_config.BaseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
        var beforeUrl = _page.Url;
        Notify($"[nav] GOTO start target='{url}' from='{beforeUrl}' pages={TryGetPageCountForDiagnostics()}");
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
        
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        Notify($"[nav] GOTO done target='{url}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        InvalidateActiveConstructionsCache();
        await ActionPacer.FromOptions(_config, Notify).DelayAsync(
            _config.ActionPacingPageLoadMinSeconds,
            _config.ActionPacingPageLoadMaxSeconds,
            cancellationToken,
            "after page load");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after navigation.", cancellationToken);
        await TryDismissContinuePromptAsync(cancellationToken);
    }

    // Reloads in place when already on the target path, otherwise navigates to it.
    private async Task ReloadOrGotoAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        if (IsCurrentUrlForPath(pathOrUrl))
        {
            Notify($"[nav] RELOAD start target='{pathOrUrl}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Notify($"[nav] RELOAD done target='{pathOrUrl}' current='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
            // A reload replaces page content just like a navigation, so any page-derived cache must
            // be dropped here too (the GotoAsync branch already does this). Without this the longer
            // active-constructions TTL could serve pre-reload state at the top of an upgrade iteration.
            InvalidateActiveConstructionsCache();
        }
        else
        {
            await GotoAsync(pathOrUrl, cancellationToken);
        }
    }

    private int TryGetPageCountForDiagnostics()
    {
        try
        {
            return _page.Context.Pages.Count;
        }
        catch
        {
            return -1;
        }
    }

    // True when Travian shows its own "auto-reload failed" UI on any timer: a <span class="timer no-reload">
    // wrapping a refresh icon (img/refresh.png). When this appears the page is stuck — the countdown has
    // expired but the expected reload never fired — and any value/level we read from it is stale. Detection
    // is a separate signal from "duration == 0" because the timer's value may be negative ("counting=down
    // value=-60") rather than zero. Callers that depend on fresh state should force a reload when this is true.
    private async Task<bool> IsPageMarkedStaleAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => !!document.querySelector('span.timer.no-reload, .timer.no-reload')
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
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

        Notify($"[ensure-logged-in] start force={force} url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        var loggedIn = await IsLoggedInAsync();
        _lastEnsureLoggedInAt = now;
        _lastEnsureLoggedInSucceeded = loggedIn;
        Notify($"[ensure-logged-in] state loggedIn={loggedIn} url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        if (!loggedIn)
        {
            // Session may have expired during a long idle wait (Travian logs the user out and
            // redirects to login.php). Re-use the existing LoginAsync flow rather than throwing —
            // BotTaskRunner already calls LoginAsync at the start of every feature, so this just
            // covers the in-feature drop case (and the keep-alive idle path). Other non-logged-in
            // states (captcha, manual_step, unknown) still need human attention, so keep throwing.
            var state = await LoginStateAsync();
            if (state == "logged_in")
            {
                _lastEnsureLoggedInAt = DateTimeOffset.UtcNow;
                _lastEnsureLoggedInSucceeded = true;
                Notify($"[ensure-logged-in] recovered after transient login-state miss url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
                if (_suppressEnsureUiSyncDepth <= 0)
                {
                    await TryEmitUiSyncSnapshotAsync(cancellationToken);
                }

                return;
            }
            if (state == "logged_out")
            {
                Notify("[ensure-logged-in] session expired — attempting auto-relogin");
                try
                {
                    await LoginAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _lastEnsureLoggedInSucceeded = false;
                    throw new InvalidOperationException($"Auto-relogin failed: {ex.Message}", ex);
                }
                _lastEnsureLoggedInAt = DateTimeOffset.UtcNow;
                _lastEnsureLoggedInSucceeded = await IsLoggedInAsync();
                if (!_lastEnsureLoggedInSucceeded)
                {
                    throw new InvalidOperationException("Auto-relogin completed but session is still not logged in.");
                }
                Notify("[ensure-logged-in] auto-relogin succeeded");
                if (_suppressEnsureUiSyncDepth <= 0)
                {
                    await TryEmitUiSyncSnapshotAsync(cancellationToken);
                }
                return;
            }

            throw new InvalidOperationException($"Not logged in. Current page state is '{state}'.");
        }
        Notify($"[ensure-logged-in] confirmed url='{_page.Url}' pages={TryGetPageCountForDiagnostics()}");
        if (_suppressEnsureUiSyncDepth <= 0)
        {
            await TryEmitUiSyncSnapshotAsync(cancellationToken);
        }
    }

    private async Task TryEmitUiSyncSnapshotAsync(CancellationToken cancellationToken, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastUiSyncAt) < UiSyncMinInterval)
        {
            return;
        }

        try
        {
            var currency = await ReadCurrencyAsync(cancellationToken);
            var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
            var villages = await ReadVillagesPreferCacheAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new UiSyncSnapshot(
                Gold: currency.Gold,
                Silver: currency.Silver,
                ActiveVillage: activeVillage,
                Villages: villages
                    .Select(v => new UiSyncVillage(v.Name, v.Url, v.IsCapital, v.CoordX, v.CoordY, v.Population, v.CropFields))
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
            // Cheap logged-out check FIRST: on a logout redirect the page is on login.php and is
            // actively navigating. Running the continue-prompt scan (which reads element text) against
            // a navigating page can hang on the default action timeout, so confirm sign-out by URL
            // before touching any element.
            var currentUrl = _page.Url.ToLowerInvariant();
            if (currentUrl.Contains("login.php", StringComparison.Ordinal))
            {
                Notify("You are logged out");
                return "logged_out";
            }

            await TryDismissContinuePromptAsync();

            if (await CaptchaOrManualStepVisibleAsync())
            {
                return "manual_step";
            }

            // The page can still be settling right after a navigation/reload (especially the
            // official React in-game pages). Probing the indicators too early yielded a false
            // 'unknown' → "Not logged in. Current page state is 'unknown'." task failures even
            // though we were on an authenticated page (e.g. dorf1.php). Retry the probe a few
            // times with a short wait before giving up. The happy path (indicator already present)
            // returns immediately on the first attempt with no delay.
            const int probeAttempts = 4;
            for (var attempt = 0; attempt < probeAttempts; attempt++)
            {
                foreach (var selector in Selectors.LoggedInIndicators)
                {
                    if (await _page.Locator(selector).CountAsync() > 0)
                    {
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

                if (attempt < probeAttempts - 1)
                {
                    // Give the DOM a moment to render the topbar/village list, then re-probe.
                    await Task.Delay(400);
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

    // Fills the login form the way a person would: type the username, pause briefly, type the
    // password, pause again before submitting. A short random 100-200ms wait between the steps is
    // enough to avoid the instant fill+submit look. Shared by the dedicated login page flow and the
    // inline "form already on page" flow.
    private async Task FillLoginCredentialsWithPacingAsync(CancellationToken cancellationToken)
    {
        await FillFirstAvailableAsync(Selectors.LoginUsernameField, _account.Username, cancellationToken);
        await Task.Delay(Random.Shared.Next(100, 201), cancellationToken);

        await FillFirstAvailableAsync(Selectors.LoginPasswordField, _account.Password, cancellationToken);
        await Task.Delay(Random.Shared.Next(100, 201), cancellationToken);
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
                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
            }, cancellationToken: cancellationToken);
            Notify("Clicked login button.");
            return;
        }

        // Fallback: no recognizable login button — submit the form by pressing Enter in the
        // password field. Many login forms submit on Enter even without a matched button selector.
        foreach (var selector in Selectors.LoginPasswordField)
        {
            var passwordField = _page.Locator(selector).First;
            if (await passwordField.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await passwordField.PressAsync("Enter", new LocatorPressOptions { Timeout = _config.TimeoutMs });
                Notify("Login button not found; submitted the form via Enter.");
                return;
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

        throw new InvalidOperationException("Could not find login button.");
    }

    private static bool IsLikelyGamePageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.Equals("/dorf1.php", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/dorf2.php", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/spieler.php", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/build.php", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/profile", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hero", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/messages", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/report", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Polls for any of the given selectors until one appears or the timeout elapses. Used to
    /// tolerate a slow-loading login page before interacting with the form.
    /// </summary>
    private async Task<bool> WaitForAnySelectorAsync(IEnumerable<string> selectors, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await HasAnySelectorAsync(selectors))
                {
                    return true;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // Page is mid-navigation; retry below.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private static readonly string[] LoginErrorPhrases =
    {
        "wrong password",
        "password is wrong",
        "password is incorrect",
        "incorrect password",
        "invalid password",
        "name or password",
        "name or the password",
        "username or password",
        "invalid credentials",
        "does not exist",
        "doesn't exist",
        "unknown user",
        "user not found",
        "account not found",
        "too many login attempts",
    };

    /// <summary>
    /// While still on the login page, returns a visible error message (wrong credentials,
    /// unknown user, etc.) if Travian rendered one, so the caller can fail fast with a clear
    /// reason instead of waiting for the full login timeout. Returns null when no error is shown.
    /// </summary>
    private async Task<string?> TryReadVisibleLoginErrorAsync()
    {
        try
        {
            if (!_page.Url.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await _page.EvaluateAsync<string?>(
                """
                (phrases) => {
                  const isVisible = (node) => {
                    if (!node || !(node instanceof Element)) return false;
                    const style = window.getComputedStyle(node);
                    if (!style || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || '1') === 0) return false;
                    const rect = node.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();

                  const errorNodes = document.querySelectorAll('.error, p.error, .errorMessage, .error_message, [class*="error" i], [class*="warning" i]');
                  for (const node of errorNodes) {
                    if (!isVisible(node)) continue;
                    const text = clean(node.innerText || node.textContent);
                    if (text && text.length > 0 && text.length <= 200) return text;
                  }

                  const bodyText = clean(document.body && document.body.innerText).toLowerCase();
                  for (const phrase of phrases) {
                    if (bodyText.includes(phrase)) return phrase;
                  }

                  return null;
                }
                """,
                LoginErrorPhrases);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
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
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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
                        // Explicit short timeout: without it InnerText falls back to the 15s default
                        // action timeout and hangs when the page is navigating (e.g. logout redirect).
                        text = (await candidate.InnerTextAsync(new LocatorInnerTextOptions { Timeout = timeoutMs }))?.Trim();
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
                        value = await candidate.GetAttributeAsync("value", new LocatorGetAttributeOptions { Timeout = timeoutMs });
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
        var timeoutSeconds = Math.Max(10, _config.ManualLoginTimeoutSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var manualMessageShown = false;
        var pollCount = 0;
        Notify($"[login:verbose] waiting for login confirmation (timeout={timeoutSeconds}s, interactive={_interactive}, browserVisible={_browserVisible})");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pollCount++;
            if (DateTime.UtcNow >= deadline)
            {
                if (!_interactive || !_browserVisible)
                {
                    Notify($"[login] timeout: login not confirmed after {timeoutSeconds}s (polls={pollCount}, headless/non-interactive)");
                    throw new InvalidOperationException("Login was not confirmed before timeout.");
                }

                if (!manualMessageShown)
                {
                    Notify("Login is not confirmed yet. Finish login/captcha in the browser if needed.");
                    manualMessageShown = true;
                }

                deadline = DateTime.UtcNow.AddSeconds(10);
            }

            try
            {
                await TryDismissContinuePromptAsync(cancellationToken);

                if (await IsLoggedInAsync())
                {
                    Notify($"[login:verbose] login confirmed after {pollCount} poll(s)");
                    return true;
                }

                // Fail fast on an explicit credential/account error instead of waiting the full
                // login timeout (which can be minutes).
                var loginError = await TryReadVisibleLoginErrorAsync();
                if (loginError is not null)
                {
                    Notify($"[login] credential/account error visible on page: {loginError}");
                    throw new InvalidOperationException($"Login failed: {loginError}");
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

        await WaitForManualVerificationToClearAsync(cancellationToken);
    }

    private async Task WaitForManualVerificationToClearAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await CaptchaOrManualStepVisibleAsync())
            {
                Notify("Manual verification cleared. Continuing.");
                return;
            }

            await Task.Delay(500, cancellationToken);
        }
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
        var safeLabel = TravianUrls.SafePathSegment(label);
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
        Notify("[scan:verbose] ReadVillagesAsync started");
        // Only navigate to spieler.php when the population cache has been explicitly invalidated
        // (i.e. on a real village switch, where SwitchToVillageAsync resets the timestamp to
        // MinValue). Otherwise serve the cache: lightweight sidebar reads and incremental
        // population updates keep it current without an expensive spieler navigation. This means
        // resource ticks / ui-sync no longer trigger a spieler read.
        var populationInvalidated = _cachedVillagesPopulationAt == DateTimeOffset.MinValue;
        if (_cachedVillages is { Count: > 0 } cached && !populationInvalidated)
        {
            return cached;
        }

        var villages = await ReadVillagesFromServerAsync(cancellationToken);
        if (villages.Count > 0)
        {
            _cachedVillages = villages.ToList();
            _cachedVillagesAt = DateTimeOffset.UtcNow;
            if (villages.Any(v => v.Population.HasValue))
            {
                _cachedVillagesPopulationAt = DateTimeOffset.UtcNow;
            }
        }
        Notify("[scan:verbose] ReadVillagesAsync finished");
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
                            // Match the prior cache by the stable village id (newdid) first so a
                            // renamed village still merges with its cached coords/population instead
                            // of being treated as a new village. Fall back to the name only when no
                            // id is available on either side.
                            var villageId = TravianUrls.TryParseNewdid(v.Url);
                            var match = villageId is not null
                                ? prior.FirstOrDefault(p => TravianUrls.TryParseNewdid(p.Url) == villageId)
                                : null;
                            match ??= prior.FirstOrDefault(p => string.Equals(p.Name, v.Name, StringComparison.Ordinal));
                            if (match is null)
                            {
                                return v;
                            }
                            return v with
                            {
                                IsCapital = v.IsCapital ?? match.IsCapital,
                                CoordX = match.CoordX,
                                CoordY = match.CoordY,
                                // Sidebar-derived population (active village, official) wins; fall
                                // back to the cached value so non-active villages keep profile data.
                                Population = v.Population ?? match.Population,
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
            Notify($"[scan:verbose] ReadVillagesPreferCache sidebar read failed, falling back: {ex.Message}");
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
              const parseCoordPart = (value) => {
                const text = clean(value).replace(/[()]/g, '');
                if (!text) return null;
                const parsed = Number.parseInt(text, 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const parseIntFromText = (value) => {
                const text = clean(value).replace(/[^\d]/g, '');
                if (!text) return null;
                const parsed = Number.parseInt(text, 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
              // Official coordinate cells contain bidi direction marks and a Unicode minus (U+2212);
              // strip/normalize them before parsing a signed integer.
              const parseSignedInt = (value) => {
                const text = clean(value)
                  .replace(/[‪-‮‎‏]/g, '')
                  .replace(/−/g, '-');
                const match = text.match(/-?\d+/);
                if (!match) return null;
                const parsed = Number.parseInt(match[0], 10);
                return Number.isFinite(parsed) ? parsed : null;
              };
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
                  const x = parseCoordPart(
                    container.querySelector('.coordinateX')?.textContent
                    || node.parentElement?.querySelector('.coordinateX')?.textContent
                    || '');
                  const y = parseCoordPart(
                    container.querySelector('.coordinateY')?.textContent
                    || node.parentElement?.querySelector('.coordinateY')?.textContent
                    || '');
                  // Official only: the active village's row carries its current population in
                  // a "div.population > span". Only the active row exposes it; read it scoped to
                  // the active container, falling back to the active sidebar entry on the page.
                  const isActive = classText.includes('active');
                  let population = null;
                  if (isActive) {
                    population = parseIntFromText(
                      container.querySelector('.population span')?.textContent
                      || document.querySelector('#sidebarBoxVillagelist .active .population span')?.textContent
                      || '');
                  }
                  rows.push({
                    name,
                    url,
                    isCapital: classText.includes('capital') ? true : null,
                    x,
                    y,
                    population,
                    isActive
                  });
                }

                if (rows.length > 0) {
                  return rows;
                }
              }

              // Official T4.6: the village list renders each entry as
              // div.listEntry.village[data-did] with a placeholder href="#" (no newdid anchor).
              // Build the switch URL from data-did so renames/new villages are picked up here too.
              for (const entry of document.querySelectorAll(
                  '#sidebarBoxVillageList .listEntry.village[data-did], .villageList .listEntry.village[data-did], .listEntry.village[data-did]')) {
                const did = (entry.getAttribute('data-did') || '').trim();
                if (!did) continue;
                const name = clean(entry.querySelector('.name')?.textContent || '');
                if (!name) continue;
                const key = `did:${did}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const classText = (entry.className || '').toLowerCase();
                const isActive = classText.includes('active');
                const x = parseSignedInt(entry.querySelector('.coordinateX')?.textContent || '');
                const y = parseSignedInt(entry.querySelector('.coordinateY')?.textContent || '');
                let population = null;
                if (isActive) {
                  // Official renders the active village's population in the active-village box
                  // (#sidebarBoxActiveVillage div.population > span), not inside the list entry.
                  population = parseIntFromText(
                    document.querySelector('#sidebarBoxActiveVillage .population span, .villageInfobox .population span, div.population span')?.textContent
                    || entry.querySelector('.population span, .population')?.textContent
                    || '');
                }
                rows.push({
                  name,
                  url: `dorf1.php?newdid=${did}`,
                  isCapital: classText.includes('capital') ? true : null,
                  x,
                  y,
                  population,
                  isActive
                });
              }

              return rows;
            }
            """);

        // Official only: trust the active village's population read from the sidebar. SS-Travi
        // markup differs, so the sidebar population is ignored there and only profile data is used.
        var useSidebarPopulation = !_config.IsPrivateServer;
        return raw
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item =>
            {
                var (cachedX, cachedY) = TryGetCachedVillageCoords(item.Name!);
                int? sidebarPopulation = null;
                if (useSidebarPopulation && item.IsActive && item.Population is int pop)
                {
                    sidebarPopulation = pop;
                    Notify($"[population] active village '{item.Name}' from sidebar = {pop}");
                }
                return new Village(
                    Name: item.Name!,
                    Url: item.Url,
                    IsCapital: item.IsCapital ?? TryGetCachedCapitalState(item.Name!),
                    CoordX: item.X ?? cachedX,
                    CoordY: item.Y ?? cachedY,
                    Population: sidebarPopulation);
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

    private async Task<IReadOnlyList<Village>> ReadVillagesFromServerAsync(
        CancellationToken cancellationToken,
        bool restorePreviousUrl = true)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages.", cancellationToken);
        var previousUrl = _page.Url;
        string? activeVillageBeforeProfile = null;
        IReadOnlyList<Village> sidebarOrder = [];
        try
        {
            activeVillageBeforeProfile = await ReadActiveVillageNameAsync(cancellationToken);
            try
            {
                // The profile table may be population-sorted. Capture the user's Travian sidebar
                // order before navigating away; profile data will enrich these rows, not order them.
                sidebarOrder = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Notify($"[scan:verbose] could not capture sidebar village order before profile read: {ex.Message}");
            }

            await GotoAsync(Paths.PlayerProfile, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages on spieler.php.", cancellationToken);
            await EnsureLoggedInAsync();

            // The official profile (contentV2) renders the villages table client-side, and the
            // population cell (td.inhabitants) is filled in slightly after the row shell. Reading
            // too early returns the village name/coords/capital but a null population. Best-effort
            // wait for a populated inhabitants cell before parsing. SS renders server-side, so this
            // is gated to official and is tolerant (timeout falls through to the parse anyway).
            if (!_config.IsPrivateServer)
            {
                try
                {
                    await _page.WaitForFunctionAsync(
                        """
                        () => {
                          const cells = document.querySelectorAll('table.villages td.inhabitants, td.inhabitants');
                          for (const cell of cells) {
                            if (/\d/.test((cell.textContent || ''))) {
                              return true;
                            }
                          }
                          return false;
                        }
                        """,
                        new PageWaitForFunctionOptions { Timeout = 3000 });
                }
                catch
                {
                    Notify("[scan:verbose] official villages table population cell not ready within wait; parsing current DOM.");
                }
            }

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
                  const parseVillageId = (value) => {
                    const source = value || '';
                    const match =
                      source.match(/[?&]newdid=(\d+)/i)
                      || source.match(/[?&]vid=(\d+)/i)
                      || source.match(/[?&]z=(\d+)/i)
                      || source.match(/[?&]d=(\d+)/i);
                    if (!match) return null;
                    const parsed = Number.parseInt(match[1], 10);
                    return Number.isFinite(parsed) ? parsed : null;
                  };
                  const resolveVillageHref = (row, preferredHref) => {
                    const preferredId = parseVillageId(preferredHref || '');
                    if (preferredId !== null && !/karte\.php/i.test(preferredHref || '')) {
                      return preferredHref || '';
                    }

                    const candidates = [
                      preferredHref || '',
                      ...Array.from(row.querySelectorAll('a[href]')).map(node => node.getAttribute('href') || '')
                    ];
                    for (const href of candidates) {
                      const villageId = parseVillageId(href);
                      if (villageId !== null) {
                        return `dorf1.php?newdid=${villageId}`;
                      }
                    }

                    return preferredHref || '';
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

                    const villageHref = resolveVillageHref(row, nameAnchor?.getAttribute('href') || '');
                    // Prefer the actual coordinate link (karte.php?x=..&y=..) over the village-name
                    // link, which on official Travian also points at karte.php but only carries ?d=<did>.
                    // Official uses td.coordinates; SS uses td.coords.
                    const coordAnchor =
                      row.querySelector('td.coordinates a[href*="x="], a[href*="karte.php?x="], a[href*="x="][href*="y="]')
                      || row.querySelector('td.coords a[href*="karte.php"], a[href*="karte.php"]');
                    const coordHref = coordAnchor?.getAttribute('href') || '';
                    const coordXText = clean(row.querySelector('td.coords .coordinateX, td.coordinates .coordinateX')?.textContent || '');
                    const coordYText = clean(row.querySelector('td.coords .coordinateY, td.coordinates .coordinateY')?.textContent || '');
                    const coordText = clean(coordAnchor?.textContent || row.querySelector('td.coords')?.textContent || '');
                    const coord = parseCoords(
                      coordHref
                      || (coordXText && coordYText ? `${coordXText}|${coordYText}` : '')
                      || coordText
                      || rowText);

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

                    // SS marks the capital with span.mainVillage; official Travian uses
                    // span.additionalInfo with the text "(Capital)" in the name cell.
                    const isCapital = !!row.querySelector('span.mainVillage, td.isCapital, .capital')
                      || Array.from(row.querySelectorAll('span.additionalInfo')).some(node => /\bcapital\b/i.test(node.textContent || ''));
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

            var profileVillages = rawList
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
                .ToList();

            if (profileVillages.Count > 0)
            {
                var ordered = new List<Village>(profileVillages.Count);
                foreach (var sidebarVillage in sidebarOrder)
                {
                    var sidebarId = TravianUrls.TryParseNewdid(sidebarVillage.Url);
                    var match = sidebarId is not null
                        ? profileVillages.FirstOrDefault(v => TravianUrls.TryParseNewdid(v.Url) == sidebarId)
                        : null;
                    match ??= profileVillages.FirstOrDefault(v =>
                        string.Equals(v.Name, sidebarVillage.Name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !ordered.Contains(match))
                    {
                        ordered.Add(match);
                    }
                }

                ordered.AddRange(profileVillages.Where(v => !ordered.Contains(v)));
                var villages = ordered
                    .OrderByDescending(v => v.IsCapital == true)
                    .ToList();
                Notify($"[villages] order source={(sidebarOrder.Count > 0 ? "sidebar" : "profile")} "
                    + $"villages={string.Join(" > ", villages.Select(v => v.Name))}");
                return villages;
            }
        }
        finally
        {
            if (restorePreviousUrl && !string.IsNullOrWhiteSpace(previousUrl))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(activeVillageBeforeProfile))
        {
            var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            if (activeCoords.X.HasValue && activeCoords.Y.HasValue && _cachedVillages is { Count: > 0 } cachedVillages)
            {
                var enriched = cachedVillages
                    .Select(v => string.Equals(v.Name, activeVillageBeforeProfile, StringComparison.Ordinal)
                        ? v with { CoordX = v.CoordX ?? activeCoords.X, CoordY = v.CoordY ?? activeCoords.Y }
                        : v)
                    .ToList();
                UpdateCachedVillages(enriched);
                SaveCachedVillageState(activeVillageBeforeProfile, null, activeCoords.X, activeCoords.Y);
                return enriched;
            }
        }

        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '#sidebarBoxVillagelist a[href*="newdid"]',
                '#sidebarBoxVillageList a[href*="newdid"]',
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

              // Official Travian (T4.6) renders the village switcher as
              // div.listEntry.village[data-did] with the name in span.name[data-did];
              // there are no newdid anchors (switching is JS-driven via data-did).
              // Reconstruct the classic switch URL dorf1.php?newdid=<did> so the rest
              // of the pipeline keeps working unchanged.
              for (const node of document.querySelectorAll('.listEntry.village[data-did], #sidebarBoxVillageList .name[data-did], span.name[data-did]')) {
                const did = node.getAttribute('data-did');
                if (!did) continue;
                const nameNode = node.classList.contains('name') ? node : node.querySelector('.name');
                const name = ((nameNode ? nameNode.textContent : node.textContent) || '').replace(/\s+/g, ' ').trim();
                const key = `${name}|${did}`;
                if (!name || seen.has(key)) continue;
                seen.add(key);
                villages.push([name, 'dorf1.php?newdid=' + did]);
              }
              if (villages.length) return JSON.stringify(villages);

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
            .ToList();
    }

    private async Task<(int? X, int? Y)> TryReadActiveVillageCoordsFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading active village coordinates.", cancellationToken);

        var coord = await _page.EvaluateAsync<ActiveVillageCoordJs>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const parseCoords = (value) => {
                const source = clean(value);
                if (!source) return { x: null, y: null };

                const query = source.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i) || source.match(/[?&]y=(-?\d+).*?[?&]x=(-?\d+)/i);
                if (query) {
                  const first = Number.parseInt(query[1], 10);
                  const second = Number.parseInt(query[2], 10);
                  if (source.toLowerCase().includes('?y=')) {
                    return { x: second, y: first };
                  }
                  return { x: first, y: second };
                }

                const pair = source.match(/\(\s*(-?\d+)\s*[|,]\s*(-?\d+)\s*\)/)
                  || source.match(/\b(-?\d+)\s*[|,]\s*(-?\d+)\b/);
                if (!pair) return { x: null, y: null };
                return {
                  x: Number.parseInt(pair[1], 10),
                  y: Number.parseInt(pair[2], 10)
                };
              };

              const candidates = [];
              const selectors = [
                '#villageNameField',
                '.villageNameField',
                '#sidebarBoxVillagelist .active',
                '#sidebarBoxVillagelist .active a[href*="newdid"]',
                '#content h1',
                '.boxTitle'
              ];

              for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                  candidates.push(clean(node.textContent || ''));
                  if (node instanceof Element) {
                    candidates.push(clean(node.getAttribute('title') || ''));
                    candidates.push(clean(node.getAttribute('aria-label') || ''));
                    for (const anchor of node.querySelectorAll('a[href*="karte.php"], a[href*="x="][href*="y="]')) {
                      candidates.push(clean(anchor.getAttribute('href') || ''));
                      candidates.push(clean(anchor.textContent || ''));
                      candidates.push(clean(anchor.getAttribute('title') || ''));
                    }

                    const next = node.nextElementSibling;
                    const prev = node.previousElementSibling;
                    if (next) candidates.push(clean(next.textContent || ''));
                    if (prev) candidates.push(clean(prev.textContent || ''));
                  }
                }
              }

              for (const value of candidates) {
                const parsed = parseCoords(value);
                if (Number.isFinite(parsed.x) && Number.isFinite(parsed.y)) {
                  return parsed;
                }
              }

              for (const anchor of document.querySelectorAll('a[href*="karte.php"], a[href*="x="][href*="y="]')) {
                const parsed = parseCoords(anchor.getAttribute('href') || anchor.textContent || '');
                if (Number.isFinite(parsed.x) && Number.isFinite(parsed.y)) {
                  return parsed;
                }
              }

              return { x: null, y: null };
            }
            """);

        return (coord?.X, coord?.Y);
    }

    private async Task<(long? Warehouse, long? Granary)> ReadStorageCapacitiesAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading storage capacity.", cancellationToken);
        var raw = await _page.EvaluateAsync<Dictionary<string, string>>(
            """
            () => {
              const readFirst = (selectors) => {
                for (const selector of selectors) {
                  for (const node of document.querySelectorAll(selector)) {
                    const value =
                      node.getAttribute('data-value')
                      || node.getAttribute('data-max')
                      || node.getAttribute('data-capacity')
                      || node.getAttribute('title')
                      || node.getAttribute('aria-label')
                      || node.textContent
                      || '';
                    const text = String(value).trim();
                    if (text) return text;
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

        raw.TryGetValue("warehouse", out var warehouseRaw);
        raw.TryGetValue("granary", out var granaryRaw);
        return (TravianParsing.TryParseResourceValue(warehouseRaw), TravianParsing.TryParseResourceValue(granaryRaw));
    }

    private async Task<(int? Gold, int? Silver)> ReadCurrencyAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading gold/silver.", cancellationToken);
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const hasGold = !!document.querySelector('#ajaxReplaceableGoldAmount_2, [id^="ajaxReplaceableGoldAmount_"], .ajaxReplaceableGoldAmount, .value.ajaxReplaceableGoldAmount, #gold');
                  const hasSilver = !!document.querySelector('#silver, #silverValue, [id^="ajaxReplaceableSilverAmount_"], .ajaxReplaceableSilverAmount, .value.ajaxReplaceableSilverAmount, [id*="silver" i], [class*="silver" i], font[color="#B3B3B3"], font[color="#b3b3b3"]');
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
            var raw = await _page.EvaluateAsync<Dictionary<string, long?>>(
                """
                () => {
                  const parseNumber = (value) => {
                    const text = (value || '')
                      .replace(/[\u202A-\u202E\u2066-\u2069]/g, '')
                      .replace(/\s+/g, ' ')
                      .trim();
                    if (!text) return null;
                    const digits = text.replace(/[^\d]/g, '');
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
                      '.value.ajaxReplaceableGoldAmount',
                      '#gold',
                      '#gold .value',
                      '[id*="gold" i]',
                      '[class*="gold" i]'
                    ])
                    ?? readFromHtmlPattern(/id=["']ajaxReplaceableGoldAmount_[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromHtmlPattern(/class=["'][^"']*\bajaxReplaceableGoldAmount\b[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromLabel(['gold', 'guld', 'premium']);

                  const silver =
                    readFirstNumber([
                      '#silver',
                      '#silverValue',
                      '[id^="ajaxReplaceableSilverAmount_"]',
                      '.ajaxReplaceableSilverAmount',
                      '.value.ajaxReplaceableSilverAmount',
                      '#sidebarBoxActiveVillage #silver',
                      '#sidebarBoxActiveVillage .silver',
                      "font[color='#B3B3B3']",
                      "font[color='#b3b3b3']"
                    ])
                    ?? readFromHtmlPattern(/id=["']ajaxReplaceableSilverAmount_[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromHtmlPattern(/class=["'][^"']*\bajaxReplaceableSilverAmount\b[^"']*["'][^>]*>([^<]+)/i)
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
                    return MergeCurrencyWithCache(ClampLongToInt32(gold), ClampLongToInt32(silver));
                }
            }

            if (attempt == 0)
            {
                var earlyLocator = await ReadCurrencyFromLocatorsAsync(cancellationToken);
                if (earlyLocator.Gold is not null || earlyLocator.Silver is not null)
                {
                    return MergeCurrencyWithCache(earlyLocator.Gold, earlyLocator.Silver);
                }
            }

            if (attempt < 4)
            {
                await Task.Delay(220, cancellationToken);
            }
        }

        var locatorCurrency = await ReadCurrencyFromLocatorsAsync(cancellationToken);
        var locatorGold = locatorCurrency.Gold;
        var locatorSilver = locatorCurrency.Silver;
        if (locatorGold is not null || locatorSilver is not null)
        {
            return MergeCurrencyWithCache(locatorGold, locatorSilver);
        }

        await LogCurrencyReadFailureDiagnosticsAsync(cancellationToken);
        if (TryGetCachedCurrency(out var cached))
        {
            var ageSeconds = _cachedCurrencyAt == DateTimeOffset.MinValue
                ? "unknown"
                : Math.Max(0, (int)(DateTimeOffset.UtcNow - _cachedCurrencyAt).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            Notify($"[resources:verbose] Could not detect live gold/silver values on this page. Using cached values from {ageSeconds}s ago: gold={cached.Gold?.ToString() ?? "-"} silver={cached.Silver?.ToString() ?? "-"}.");
            return cached;
        }

        Notify("Could not detect gold/silver values on this page and no cached values are available. Returning '-'.");
        return (null, null);
    }

    private async Task<(int? Gold, int? Silver)> ReadCurrencyFromLocatorsAsync(CancellationToken cancellationToken)
    {
        var gold = await ReadNumberFromSelectorsAsync(
            [
                "#ajaxReplaceableGoldAmount_2",
                "[id^='ajaxReplaceableGoldAmount_']",
                ".ajaxReplaceableGoldAmount",
                ".value.ajaxReplaceableGoldAmount",
                "#gold",
                "[id*='gold' i]"
            ],
            cancellationToken);
        var silver = await ReadNumberFromSelectorsAsync(
            [
                "#silver",
                "#silverValue",
                "[id^='ajaxReplaceableSilverAmount_']",
                ".ajaxReplaceableSilverAmount",
                ".value.ajaxReplaceableSilverAmount",
                "#sidebarBoxActiveVillage #silver",
                "#sidebarBoxActiveVillage .silver",
                "font[color='#B3B3B3']",
                "font[color='#b3b3b3']"
            ],
            cancellationToken);
        return (gold, silver);
    }

    private (int? Gold, int? Silver) MergeCurrencyWithCache(int? liveGold, int? liveSilver)
    {
        var gold = liveGold ?? _cachedGold;
        var silver = liveSilver ?? _cachedSilver;
        _cachedGold = gold;
        _cachedSilver = silver;
        _cachedCurrencyAt = DateTimeOffset.UtcNow;
        return (gold, silver);
    }

    private bool TryGetCachedCurrency(out (int? Gold, int? Silver) currency)
    {
        currency = (_cachedGold, _cachedSilver);
        return _cachedGold is not null || _cachedSilver is not null;
    }

    private async Task LogCurrencyReadFailureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var diagnostics = await _page.EvaluateAsync<string>(
                """
                () => JSON.stringify({
                  url: window.location.href,
                  title: document.title || '',
                  bodyClass: document.body?.className || '',
                  goldCount: document.querySelectorAll('#ajaxReplaceableGoldAmount_2, [id^="ajaxReplaceableGoldAmount_"], .ajaxReplaceableGoldAmount, .value.ajaxReplaceableGoldAmount, #gold').length,
                  silverCount: document.querySelectorAll('#silver, #silverValue, [id^="ajaxReplaceableSilverAmount_"], .ajaxReplaceableSilverAmount, .value.ajaxReplaceableSilverAmount').length,
                  currencyLikeCount: document.querySelectorAll('[class*="currency" i], [id*="currency" i], [class*="gold" i], [id*="gold" i], [class*="silver" i], [id*="silver" i]').length,
                  dialogCount: document.querySelectorAll('dialog, [role="dialog"], .dialog, #dialogContent, .popup').length
                })
                """);
            Notify($"Currency read diagnostics: {diagnostics}");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"Currency read diagnostics skipped: {ex.Message}");
        }
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
                var parsed = TravianParsing.ParseNumericTextToInt(text);
                if (parsed is not null)
                {
                    return parsed;
                }

                var title = await locator.GetAttributeAsync("title");
                parsed = TravianParsing.ParseNumericTextToInt(title);
                if (parsed is not null)
                {
                    return parsed;
                }

                var aria = await locator.GetAttributeAsync("aria-label");
                parsed = TravianParsing.ParseNumericTextToInt(aria);
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

    private async Task<string> ReadActiveVillageNameAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the active village.", cancellationToken);
        var value = await _page.EvaluateAsync<string>(
            """
            () => {
              // Strip Unicode bidi/direction marks (Travian wraps coords in them) + normalize the
              // U+2212 minus, then collapse whitespace.
              const clean = (raw) => (raw || '')
                .replace(/[‪-‮⁦-⁩‎‏]/g, '')
                .replace(/−/g, '-')
                .replace(/\s+/g, ' ')
                .trim();

              // Official T4.6: the active village is the highlighted sidebar entry. Read ONLY its name
              // span so the result is the clean village name, not "GREZ(-27|-66)" (name + coordinates).
              const nameSpan = document.querySelector(
                '.listEntry.village.active .name, #sidebarBoxVillagelist .active .name, .villageList .active .name');
              const spanText = clean(nameSpan ? nameSpan.textContent : '');
              if (spanText) return spanText;

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
                // Drop a trailing coordinate part "(x|y)" that some headers append after the name.
                const text = clean(element ? element.textContent : '').replace(/\s*\([^)]*\)\s*$/, '').trim();
                if (text) return text;
              }

              return 'Unknown village';
            }
            """);
        return string.IsNullOrWhiteSpace(value) ? "Unknown village" : value;
    }

    private async Task<string> ReadTribeAsync(CancellationToken cancellationToken)
    {
        var cached = KnownTribe;
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

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

              // Most reliable on official Travian (T4.6): building slots and their images carry
              // the village tribe as a CSS class token, e.g. "buildingSlot a19 g0 aid19 gaul"
              // and "building g23 gaul". Use getAttribute so SVG className objects are handled.
              const classNorm = (raw) => {
                const c = (raw || '').toLowerCase();
                if (/\bgaul\b/.test(c)) return 'Gauls';
                if (/\bteuton\b/.test(c)) return 'Teutons';
                if (/\broman\b/.test(c)) return 'Romans';
                if (/\begyptian\b/.test(c)) return 'Egyptians';
                if (/\bhun\b/.test(c)) return 'Huns';
                if (/\bspartan\b/.test(c)) return 'Spartans';
                return null;
              };
              for (const node of document.querySelectorAll('div.buildingSlot, img.building')) {
                const fromClass = classNorm(node.getAttribute('class'));
                if (fromClass) return fromClass;
              }

              // Primary: tribe icon img (works directly from dorf1/dorf2).
              for (const img of document.querySelectorAll('img.nationBig, img[src*="/tribes/"], img[src*="nation"], img[alt]')) {
                const fromAlt = altNorm(img.getAttribute('alt'));
                if (fromAlt) return fromAlt;
                const fromSrc = srcNorm(img.getAttribute('src'));
                if (fromSrc) return fromSrc;
              }

              // Note: a bare 'body' catch-all was removed — scanning the whole page text for a
              // tribe word false-matched a stray "roman" on official Travian and got cached.
              // Returning 'Unknown' (not cached, retried on dorf2) is safer than a wrong tribe.
              const selectors = [
                '[class*="tribe" i]',
                '[id*="tribe" i]',
                '.playerInfo',
                '#sidebarBoxActiveVillage',
                '#sidebarBoxVillagelist'
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

        if (!IsKnownTribe(value))
        {
            return "Unknown";
        }

        _sessionTribe = value;
        _cachedTribe = value;
        _cachedTribePlusAt = DateTimeOffset.UtcNow;
        return value;
    }

    private async Task<bool> ReadGoldClubEnabledAsync(CancellationToken cancellationToken)
    {
        if (_cachedGoldClubEnabled == true)
        {
            return true;
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading gold club status.", cancellationToken);
        var enabled = await _page.EvaluateAsync<bool>(
            """
            () => {
              // Official Travian (T4.6) embeds a GraphQL bootstrap state blob in the page that carries
              // the account's gold features, e.g. "goldFeatures":{"travianPlus":{"isActive":true},"goldClub":true}.
              // This is the reliable signal: read the boolean directly (the unquoted "goldClub" in the
              // query definition has no colon+boolean, so it never matches).
              const html = document.documentElement ? document.documentElement.innerHTML : '';
              const match = html.match(/"goldClub"\s*:\s*(true|false)/i);
              if (match) return match[1].toLowerCase() === 'true';

              // Fallback for server variants without the GraphQL blob (e.g. SS-Travi): the Gold Club
              // master-build button, or a builder marker in the village-list sidebar.
              if (document.querySelector('#buttonBuild')) return true;
              const sidebar = document.querySelector('#sidebarBoxVillagelist');
              return /buildOff|buildOn|builder=On/.test(sidebar?.innerHTML || '');
            }
            """);

        if (enabled)
        {
            _cachedGoldClubEnabled = true;
        }

        return enabled;
    }

    private static bool IsKnownTribe(string? tribe)
        => !string.IsNullOrWhiteSpace(tribe)
           && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase);

    private async Task EnsureRallyPointAndOpenFarmListPageAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(RallyPointFarmListPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening farmlists.", cancellationToken);
        await EnsureLoggedInAsync();
        await WaitForOfficialFarmListRenderAsync(cancellationToken);
        if (await IsFarmListPageAsync(cancellationToken))
        {
            return;
        }

        // Farm lists require a built Rally Point. When it is still level 0 (not built) the rally point
        // page shows the construct view instead of the farm lists — abort with a clear message rather
        // than auto-building it, so the user decides when to build it.
        if (await IsRallyPointLevelZeroAsync(cancellationToken))
        {
            throw new InvalidOperationException("Rally Point is level 0 (not built) in this village. Build the Rally Point before using farm lists.");
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

        await GotoAsync(RallyPointFarmListPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening farmlists.", cancellationToken);
        await EnsureLoggedInAsync();
        await WaitForOfficialFarmListRenderAsync(cancellationToken);

        if (!await IsFarmListPageAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Could not open farm list page at {RallyPointFarmListPath}. Farmlists may be unavailable on this account/server.");
        }
    }

    private async Task WaitForOfficialFarmListRenderAsync(CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            return;
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => !!document.querySelector('#rallyPointFarmList')",
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] Official farm list root did not render within 5 seconds; continuing with page checks.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureOfficialFarmListsExpandedAsync(CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            return;
        }

        var collapsedCount = await _page.EvaluateAsync<int>(
            """
            () => {
              const collapsed = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper.collapsed'));
              for (const list of collapsed) {
                const toggle = list.querySelector('.farmListHeader .expandCollapse');
                toggle?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              }
              return collapsed.length;
            }
            """);
        if (collapsedCount <= 0)
        {
            return;
        }

        Notify($"[farm-list] expanding {collapsedCount} Official farm list(s) to read target coordinates");
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const clean = (value) => (value || '')
                    .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                    .replace(/\s+/g, ' ')
                    .trim();
                  return Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                    .every(list => {
                      if (list.classList.contains('collapsed')) return false;
                      const match = clean(list.querySelector('td.addTarget')?.textContent).match(/(\d+)\s*\/\s*(\d+)/);
                      const expectedRows = match ? Number(match[1]) : 0;
                      return list.querySelectorAll('tbody tr.slot').length >= expectedRows;
                    });
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 8000 });
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] some Official farm lists did not expand within 8 seconds; reading available targets.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureRallyPointAndOpenSendTroopsPageAsync(CancellationToken cancellationToken, bool allowReuseCurrentPage)
    {
        if (allowReuseCurrentPage && await IsSendTroopsPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(RallyPointSendTroopsPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening send troops.", cancellationToken);
        await EnsureLoggedInAsync();
        if (await IsSendTroopsPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync(Paths.FarmListFastUp, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening rally point slot.", cancellationToken);
        await EnsureLoggedInAsync();

        await GotoAsync(RallyPointSendTroopsPath, cancellationToken);
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
            throw new InvalidOperationException("Rally Point does not appear to be constructed yet. Build Rally Point before sending troops.");
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

    // True when the current page is the Rally Point build view showing "Level 0" — i.e. the Rally Point
    // is not built yet, so farm lists are unavailable. Scoped to the rally point construct view
    // (#content.buildRallyPoint), so an open farm list (built rally point) never matches.
    private async Task<bool> IsRallyPointLevelZeroAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const content = document.querySelector('#content.buildRallyPoint');
                  if (!content) return false;
                  const levelEl = content.querySelector('.titleInHeader .level');
                  const match = (levelEl?.textContent || '').match(/(\d+)/);
                  return match ? parseInt(match[1], 10) === 0 : false;
                }
                """);
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-list] could not read Rally Point level: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> IsSendTroopsPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while checking send troops page.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const hasCoords = !!document.querySelector('input[name="x"], input[name="y"], input[name*="xCoord" i], input[name*="yCoord" i], input[id*="xCoord" i], input[id*="yCoord" i]');
              // SS uses radio name="c"; official Travian (T4.6) uses name="eventType".
              const hasAttackMode = !!document.querySelector('input[type="radio"][name="c"], input[type="radio"][name="eventType"]');
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
              const cleanNumericText = (value) =>
                normalize(value).replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '');

              // Official Travian (T4.6) keeps list metadata rendered even when lists are collapsed.
              const officialCandidates = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              if (officialCandidates.length > 0) {
                return officialCandidates.slice(0, 200).map((candidate) => {
                  const name =
                    normalize(candidate.querySelector('.farmListName .name')?.textContent) ||
                    'Farm list';
                  const lid =
                    candidate.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
                    candidate.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
                    '';

                  const statusText = cleanNumericText(candidate.querySelector('.farmListStatus')?.textContent);
                  const statusMatch = statusText.match(/(\d+)\s*\/\s*(\d+)/);
                  const running = statusMatch ? Number(statusMatch[1]) : 0;
                  const statusTotal = statusMatch ? Number(statusMatch[2]) : 0;

                  const capacityText = cleanNumericText(candidate.querySelector('td.addTarget')?.textContent);
                  const capacityMatch = capacityText.match(/(\d+)\s*\/\s*(\d+)/);
                  let total = capacityMatch ? Number(capacityMatch[1]) : statusTotal;
                  let capacity = capacityMatch ? Number(capacityMatch[2]) : total;

                  const startButton = candidate.querySelector('button.startFarmList');
                  const startText = cleanNumericText(startButton?.textContent);
                  const startCountMatch = startText.match(/start\s*\((\d+)\)/i);
                  const renderedSlots = Array.from(candidate.querySelectorAll('tbody tr.slot'));
                  let active = startCountMatch
                    ? Number(startCountMatch[1])
                    : renderedSlots.filter((row) => !row.classList.contains('disabled')).length;

                  if (!Number.isFinite(total) || total < 0) total = 0;
                  if (!Number.isFinite(capacity) || capacity < total) capacity = total;
                  if (!Number.isFinite(active) || active < 0) active = 0;
                  if (renderedSlots.length === 0 && !startCountMatch && startButton && !startButton.classList.contains('disabled')) {
                    active = total;
                  }
                  if (total > 0 && active > total) active = total;

                  const farmCoordinates = [];
                  const seenCoordinates = new Set();
                  for (const link of candidate.querySelectorAll('tbody tr.slot td.target a[href*="karte.php"]')) {
                    const href = link.getAttribute('href') || '';
                    const match = href.match(/[?&]x=(-?\d+).*?[?&]y=(-?\d+)/i);
                    if (!match) continue;
                    const key = `${Number(match[1])}|${Number(match[2])}`;
                    if (seenCoordinates.has(key)) continue;
                    seenCoordinates.add(key);
                    farmCoordinates.push(key);
                  }

                  return {
                    name,
                    activeFarmCount: active,
                    totalFarmCount: total,
                    capacity,
                    farmCoordinates,
                    timerText: '',
                    disabled: Number.isFinite(running) && running > 0,
                    lid
                  };
                });
              }

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

                // Resolve the farm list id (lid). The Start Raid button id encodes the lid
                // (startRaidBtnTop<lid>), but the countdown span id (timerTop<n>) uses an
                // unrelated sequential index, so we must read the timer from the button itself.
                const tryReadListId = (root) => {
                  if (!root) return null;
                  const markAll = root.querySelector('input[id^="raidListMarkAll"]');
                  const markAllMatch = (markAll?.id || '').match(/raidListMarkAll(\d+)/i);
                  if (markAllMatch) return markAllMatch[1];
                  const btn = root.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                  const btnIdMatch = (btn?.id || '').match(/startRaidBtnTop(\d+)/i);
                  if (btnIdMatch) return btnIdMatch[1];
                  if (btn?.getAttribute('data-lid')) return btn.getAttribute('data-lid');
                  const switchNode = root.querySelector('.openedClosedSwitch[onclick*="toggleList"]');
                  const switchMatch = (switchNode?.getAttribute('onclick') || '').match(/toggleList\((\d+)\)/i);
                  if (switchMatch) return switchMatch[1];
                  return null;
                };

                const lid =
                  tryReadListId(candidate) ||
                  tryReadListId(container) ||
                  tryReadListId(candidate.closest('.listTitle')?.parentElement || null);

                let raidButton = null;
                if (lid) {
                  raidButton =
                    document.getElementById(`startRaidBtnTop${lid}`) ||
                    document.querySelector(`button.startRaidButton[data-lid="${lid}"]`);
                }
                if (!raidButton) {
                  raidButton = container.querySelector('button[id^="startRaidBtnTop"], button.startRaidButton[data-lid]');
                }

                const readTimerFrom = (root) => {
                  if (!root) return '';
                  const span = root.querySelector('span[id^="timerTop"]');
                  const spanText = normalize(span?.textContent);
                  if (/\d{1,3}:\d{2}/.test(spanText)) return spanText;
                  const contentText = normalize(root.querySelector('.button-content')?.textContent || root.textContent);
                  const match = contentText.match(/\d{1,3}:\d{2}(?::\d{2})?/);
                  return match ? match[0] : '';
                };

                const timerText = readTimerFrom(raidButton) || readTimerFrom(container);

                let disabled = false;
                if (raidButton) {
                  const cls = (raidButton.className || '').toLowerCase();
                  disabled = !!raidButton.disabled || raidButton.getAttribute('disabled') !== null || cls.includes('disabled');
                }

                const key = name.toLowerCase();
                const existing = seenByName.get(key);
                if (!existing) {
                  seenByName.set(key, { name, activeFarmCount: active, totalFarmCount: total, timerText, disabled, lid: lid || '' });
                  continue;
                }

                seenByName.set(key, {
                  name,
                  activeFarmCount: Math.max(existing.activeFarmCount || 0, active),
                  totalFarmCount: Math.max(existing.totalFarmCount || 0, total),
                  timerText: (existing.timerText && existing.timerText.length > 0) ? existing.timerText : timerText,
                  disabled: existing.disabled || disabled,
                  lid: (existing.lid && existing.lid.length > 0) ? existing.lid : (lid || '')
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
            .Select(row =>
            {
                var remainingSeconds = ResolveFarmListRemainingSeconds(row.TimerText, row.Disabled);
                return new FarmListOverview(
                    Name: row.Name!,
                    ActiveFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.ActiveFarmCount ?? 0)),
                    TotalFarmCount: Math.Min(MaxFarmsPerFarmList, Math.Max(0, row.TotalFarmCount ?? 0)),
                    RemainingSeconds: remainingSeconds,
                    ListId: string.IsNullOrWhiteSpace(row.Lid) ? null : row.Lid!.Trim(),
                    Capacity: row.Capacity,
                    FarmCoordinates: row.FarmCoordinates ?? [],
                    Finish: remainingSeconds is > 0 ? TimerSnapshot.FromRemaining(remainingSeconds.Value) : null);
            })
            .ToList();
    }

    private static int? ResolveFarmListRemainingSeconds(string? timerText, bool disabled)
    {
        var seconds = TravianParsing.ParseDurationToSeconds(timerText);
        if (seconds is > 0)
        {
            return seconds;
        }

        // The Start Raid button is disabled while a raid timer is running. If we could not parse
        // the exact countdown text, still report a running timer so the UI does not show "Ready".
        return disabled ? 1 : seconds;
    }

    private async Task WaitForDispatchLimitToClearAsync(CancellationToken cancellationToken)
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
        Notify($"[farm-list] dispatch limit active — deferring farming for {waitSeconds}s");
        throw new InvalidOperationException($"Farm dispatch limit active. queue_wait_seconds={waitSeconds}");
    }

    private async Task<FarmListSendAllClickStateJs> TryClickStartAllFarmListsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before starting all farmlists.", cancellationToken);
        var state = await _page.EvaluateAsync<FarmListSendAllClickStateJs>(
            """
            () => {
              const readListId = (wrapper) =>
                wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
                wrapper?.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
                '';
              const isDisabled = (node) => {
                const cls = (node?.getAttribute('class') || '').toLowerCase();
                return !node || node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled');
              };

              const wrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              const listIds = wrappers
                .filter(wrapper => wrapper.querySelector('button.startFarmList'))
                .map(readListId)
                .filter(id => id && id.length > 0);
              const allButton = document.querySelector('#rallyPointFarmList button.startAllFarmLists, button.startAllFarmLists');
              if (!allButton) {
                return { clicked: false, reason: 'start-all button not found', listIds, listCount: listIds.length };
              }

              if (isDisabled(allButton)) {
                return { clicked: false, reason: 'start-all button disabled', listIds, listCount: listIds.length };
              }

              allButton.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              allButton.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              allButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return { clicked: true, reason: '', listIds, listCount: listIds.length };
            }
            """).WaitAsync(cancellationToken);
        return state ?? new FarmListSendAllClickStateJs { Clicked = false, Reason = "start-all state could not be read" };
    }

    private async Task WaitForFarmListStartButtonsDisabledAsync(IReadOnlyList<string> listIds, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                FarmListStartButtonsDisabledScript,
                listIds.ToArray(),
                new PageWaitForFunctionOptions { Timeout = 15000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[farm-list] send-all buttons did not visibly disable within 15 seconds; continuing to completion wait.");
        }
    }

    private async Task WaitForFarmListStartButtonsEnabledAsync(IReadOnlyList<string> listIds, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                FarmListStartButtonsEnabledScript,
                listIds.ToArray(),
                new PageWaitForFunctionOptions { Timeout = 120000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("Start all farmlists did not finish within 120 seconds; Start buttons were still disabled.", ex);
        }
    }

    private const string FarmListStartButtonsDisabledScript =
        """
        (ids) => {
          const wanted = new Set((ids || []).map(String).filter(Boolean));
          const readListId = (button) => {
            const wrapper = button.closest('.farmListWrapper');
            return wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
              wrapper?.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
              '';
          };
          const isDisabled = (node) => {
            const cls = (node?.getAttribute('class') || '').toLowerCase();
            return !!node && (node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled'));
          };
          const buttons = Array.from(document.querySelectorAll('#rallyPointFarmList button.startFarmList, button.startFarmList'))
            .filter(button => wanted.size === 0 || wanted.has(readListId(button)));
          return buttons.length === 0 || buttons.some(isDisabled);
        }
        """;

    private const string FarmListStartButtonsEnabledScript =
        """
        (ids) => {
          const wanted = new Set((ids || []).map(String).filter(Boolean));
          const readListId = (button) => {
            const wrapper = button.closest('.farmListWrapper');
            return wrapper?.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') ||
              wrapper?.querySelector('[data-farm-list-id]')?.getAttribute('data-farm-list-id') ||
              '';
          };
          const isDisabled = (node) => {
            const cls = (node?.getAttribute('class') || '').toLowerCase();
            return !!node && (node.disabled || node.getAttribute('disabled') !== null || cls.includes('disabled'));
          };
          const buttons = Array.from(document.querySelectorAll('#rallyPointFarmList button.startFarmList, button.startFarmList'))
            .filter(button => wanted.size === 0 || wanted.has(readListId(button)));
          return buttons.length === 0 || buttons.every(button => !isDisabled(button));
        }
        """;

    private async Task<IReadOnlyList<FarmListLossRowJs>> ReadFarmListLossRowsFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while scanning farm losses.", cancellationToken);
        var rows = await _page.EvaluateAsync<FarmListLossRowJs[]>(
            """
            () => {
              const clean = (value) => (value || '')
                .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                .replace(/\s+/g, ' ')
                .trim();
              const classText = (node) => {
                if (!node) return '';
                const value = node.getAttribute ? node.getAttribute('class') : '';
                return typeof value === 'string' ? value : '';
              };
              const rows = Array.from(document.querySelectorAll('#rallyPointFarmList tr.slot, tr.slot'));
              return rows.map((row, rowIndex) => {
                const raidClasses = [];
                const lastRaid = row.querySelector('td.lastRaid');
                if (lastRaid) {
                  raidClasses.push(classText(lastRaid));
                  lastRaid.querySelectorAll('[class]').forEach(node => raidClasses.push(classText(node)));
                }

                const input = row.querySelector('input[data-slot-id]');
                const wrapper = row.closest('.farmListWrapper');
                return {
                  rowIndex,
                  slotId: input?.getAttribute('data-slot-id') || '',
                  listName: clean(wrapper?.querySelector('.farmListName .name')?.textContent || ''),
                  targetName: clean(row.querySelector('td.target a')?.textContent || row.querySelector('td.target')?.textContent || ''),
                  rowClass: classText(row),
                  raidClass: raidClasses.filter(Boolean).join(' '),
                  disabled: row.classList.contains('disabled')
                };
              });
            }
            """).WaitAsync(cancellationToken);
        return rows ?? [];
    }

    private static bool IsFarmListLossRow(FarmListLossRowJs row)
    {
        return row is { Disabled: false }
            && FarmListLossStateClassifier.Classify(row.RaidClass) == FarmListLossState.Loss;
    }

    private static bool IsFarmListLossDeactivationCandidate(FarmListLossRowJs row, bool includeUnoccupiedOasis)
    {
        return IsFarmListLossRow(row)
            && (includeUnoccupiedOasis || !FarmListLossStateClassifier.IsUnoccupiedOasis(row.TargetName));
    }

    private async Task<bool> TryDeactivateFarmListLossRowAsync(FarmListLossRowJs row, CancellationToken cancellationToken)
    {
        var menuOpened = await _page.EvaluateAsync<bool>(
            """
            (candidate) => {
              const findRow = () => {
                if (candidate.slotId) {
                  const bySlot = document.querySelector(`#rallyPointFarmList tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"], tr.slot input[data-slot-id="${CSS.escape(candidate.slotId)}"]`);
                  if (bySlot) return bySlot.closest('tr.slot');
                }

                const rows = Array.from(document.querySelectorAll('#rallyPointFarmList tr.slot, tr.slot'));
                return rows[candidate.rowIndex] || null;
              };
              const row = findRow();
              if (!row) return false;
              row.scrollIntoView({ block: 'center' });
              const trigger = row.querySelector('td.openContextMenu a, td.openContextMenu button, td.openContextMenu');
              if (!trigger) return false;
              trigger.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              trigger.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              trigger.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """,
            new { slotId = row.SlotId ?? string.Empty, rowIndex = row.RowIndex }).WaitAsync(cancellationToken);
        if (!menuOpened)
        {
            return false;
        }

        await Task.Delay(150, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before deactivating farm loss row.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const entries = Array.from(document.querySelectorAll('.entry.deactivate, button.entry.deactivate, [class~="deactivate"]'));
              const entry = entries.find(node => clean(node.textContent).includes('deactivate'));
              if (!entry) return false;
              entry.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
              entry.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """).WaitAsync(cancellationToken);
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

              // Official Travian (T4.6): each list is a #rallyPointFarmList .farmListWrapper with the
              // name in .farmListName .name and a single "Start (N)" button.startFarmList. Clicking it
              // sends every selected target, so no mark-all checkbox is needed.
              const officialWrappers = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'));
              for (const wrapper of officialWrappers) {
                const name = normalizeListName(wrapper.querySelector('.farmListName .name')?.textContent || '');
                if (name !== target) continue;

                const startButton = wrapper.querySelector('button.startFarmList');
                if (!startButton) return false;

                const startClass = (startButton.className || '').toLowerCase();
                if (startButton.disabled || startClass.includes('disabled')) return false;

                startButton.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
                startButton.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
                startButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                return true;
              }

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

        return TravianParsing.ParseDurationToSeconds(rawTimer);
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
        HashSet<string>? persistedEnabledKeys = null;
        if (!forceRefresh)
        {
            if (_natarFarmCacheStore.TryLoad(AccountName, out var persisted, ServerUrl, selectionMode)
                && persisted is not null
                && persisted.Coordinates.Count > 0)
            {
                var restored = persisted.Coordinates
                    .Where(item => item.Enabled)
                    .Select(item => new NatarCoordinateJs { X = item.X, Y = item.Y, VillageName = item.VillageName })
                    .ToList();
                lock (NatarCacheSync)
                {
                    CachedNatarCoordinatesByAccount[cacheKey] = [.. restored];
                }

                Notify($"[farm-natar] using persisted farms list — {restored.Count} enabled target(s)");
                return restored;
            }

            lock (NatarCacheSync)
            {
                if (CachedNatarCoordinatesByAccount.TryGetValue(cacheKey, out var existing) && existing.Count > 0)
                {
                    Notify($"[farm-natar] using in-memory cached farms list — {existing.Count} target(s)");
                    return [.. existing];
                }
            }
        }
        else if (_natarFarmCacheStore.TryLoad(AccountName, out var persisted, ServerUrl, selectionMode)
            && persisted is not null
            && persisted.Coordinates.Count > 0)
        {
            persistedEnabledKeys = persisted.Coordinates
                .Where(item => item.Enabled)
                .Select(item => NatarFarmCacheStore.BuildCoordinateKey(item.X, item.Y))
                .ToHashSet(StringComparer.Ordinal);
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
                    .Select(item =>
                    {
                        var key = NatarFarmCacheStore.BuildCoordinateKey(item.X!.Value, item.Y!.Value);
                        var enabled = persistedEnabledKeys is null || persistedEnabledKeys.Contains(key);
                        return new NatarFarmCoordinate(item.X!.Value, item.Y!.Value, item.VillageName, enabled);
                    })
                    .ToList()));

            Notify(changed
                ? $"[farm-natar] scanned profile — saved {cached.Count} target(s) to cache"
                : $"[farm-natar] scanned profile — confirmed {cached.Count} existing cached target(s)");
        }
        else
        {
            Notify("[farm-natar] scanned profile but found no targets (empty Natar list?)");
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

              for (const wrapper of document.querySelectorAll('#rallyPointFarmList .farmListWrapper')) {
                const name = normalizeListName(wrapper.querySelector('.farmListName .name')?.textContent);
                if (name !== target) continue;
                const listId = wrapper.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list');
                if (listId) return listId;
              }

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

    private async Task OpenAddRaidFormAsync(string lid, CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            await GotoAsync(Paths.FarmListBySlotId(lid), cancellationToken);
        }
        else
        {
            if (!await IsFarmListPageAsync(cancellationToken))
            {
                await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
            }

            await DelayFarmListStepAsync(cancellationToken);
            var clicked = await _page.EvaluateAsync<bool>(
                """
                (listId) => {
                  const wrapper = Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper'))
                    .find(node => node.querySelector('.dragAndDrop[data-list]')?.getAttribute('data-list') === String(listId));
                  const button = wrapper?.querySelector('td.addTarget a, td.addTarget button');
                  if (!button) return false;
                  button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                  return true;
                }
                """,
                lid);
            if (!clicked)
            {
                throw new InvalidOperationException($"Could not open Add target for farm list id {lid}.");
            }

            await DelayFarmListStepAsync(cancellationToken);

            // Wait for the Add target form (X/Y inputs) to render. Retry up to 3 times with a 10s
            // timeout each in case the page is slow to mount the dialog.
            const int formRenderAttempts = 3;
            var formRendered = false;
            for (var attempt = 1; attempt <= formRenderAttempts; attempt++)
            {
                try
                {
                    await _page.WaitForFunctionAsync(
                        """
                        () => {
                          const form = document.querySelector('#farmListTargetForm');
                          return !!form?.querySelector('input[name="x"]')
                            && !!form?.querySelector('input[name="y"]');
                        }
                        """,
                        null,
                        new PageWaitForFunctionOptions { Timeout = 10000 });
                    formRendered = true;
                    break;
                }
                catch (TimeoutException)
                {
                    Notify($"[farm-list] Add target dialog for farm list id {lid} not rendered (attempt {attempt}/{formRenderAttempts}).");
                }
            }

            if (!formRendered)
            {
                throw new InvalidOperationException($"Add target dialog for farm list id {lid} did not render after {formRenderAttempts} attempts.");
            }
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Add Raid form.", cancellationToken);
        await EnsureLoggedInAsync();
    }

    private async Task<AddRaidSaveOutcome> TryFillAddRaidFormAndSaveAsync(
        string farmListName,
        string troopType,
        int troopCount,
        int x,
        int y,
        string lid,
        bool useDefaultTroops,
        CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before filling Add Raid form.", cancellationToken);
        var troopIndex = TroopCatalog.ResolveTroopIndex(troopType);
        var filled = await _page.EvaluateAsync<bool>(
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

              const root = document.querySelector('#farmListTargetForm') || document;
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

              const xInput =
                root.querySelector('input[name="x"], input[name="xCoord"], input[id*="xCoord" i]') ||
                findInput(['xcoord', 'coordx', 'x']);
              const yInput =
                root.querySelector('input[name="y"], input[name="yCoord"], input[id*="yCoord" i]') ||
                findInput(['ycoord', 'coordy', 'y']);
              if (!xInput || !yInput) return false;

              const listSelect = root.querySelector('select[name="listId"]') ||
                selects.find(select => Array.from(select.options || []).some(option => option.value === String(args.lid)));
              if (listSelect && listSelect.value !== String(args.lid)) {
                const option = Array.from(listSelect.options || []).find(opt => opt.value === String(args.lid));
                if (option) {
                  listSelect.value = option.value;
                  listSelect.dispatchEvent(new Event('input', { bubbles: true }));
                  listSelect.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              const listSelectByName = selects.find(select => Array.from(select.options || []).some(option => norm(option.textContent || '') === norm(args.farmListName)));
              if (!listSelect && listSelectByName) {
                const option = Array.from(listSelectByName.options || []).find(opt => norm(opt.textContent || '') === norm(args.farmListName));
                if (option) {
                  listSelectByName.value = option.value;
                  listSelectByName.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              if (args.pacedCoordinateInputs) {
                return true;
              }

              if (!setValue(xInput, args.x)) return false;
              if (!setValue(yInput, args.y)) return false;

              if (args.useDefaultTroops) {
                return true;
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
                return (args.troopIndex && (name === `t${args.troopIndex}` || id === `t${args.troopIndex}`))
                  || id.includes('count') || id.includes('amount') || name.includes('count') || name.includes('amount');
              });
              if (!countInput) {
                countInput = textInputs.find(node => node !== xInput && node !== yInput);
              }
              if (!countInput) return false;
              if (!setValue(countInput, args.troopCount)) return false;

              return true;
            }
            """,
            new
            {
                farmListName,
                troopType,
                troopCount,
                troopIndex,
                lid,
                useDefaultTroops,
                pacedCoordinateInputs = !_config.IsPrivateServer,
                x,
                y,
            });

        if (!filled)
        {
            return AddRaidSaveOutcome.Failed;
        }

        if (!_config.IsPrivateServer)
        {
            await DelayFarmListStepAsync(cancellationToken);
            var xInput = _page.Locator(
                "#farmListTargetForm input[name=\"x\"], " +
                "#farmListTargetForm input[name=\"xCoord\"], " +
                "#farmListTargetForm input[id*=\"xCoord\" i]").First;
            await xInput.FillAsync(x.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Notify($"[farm-list] Add target X filled with {x} for '{farmListName}'.");
            await DelayFarmListStepAsync(cancellationToken);

            var yInput = _page.Locator(
                "#farmListTargetForm input[name=\"y\"], " +
                "#farmListTargetForm input[name=\"yCoord\"], " +
                "#farmListTargetForm input[id*=\"yCoord\" i]").First;
            await yInput.FillAsync(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Notify($"[farm-list] Add target Y filled with {y} for '{farmListName}'.");
            await DelayFarmListStepAsync(cancellationToken);

            if (useDefaultTroops)
            {
                var validationTriggered = await _page.EvaluateAsync<bool>(
                    """
                    () => {
                      const form = document.querySelector('#farmListTargetForm');
                      if (!form) return false;

                      const active = document.activeElement;
                      if (active instanceof HTMLElement) {
                        active.blur();
                      }

                      const clickTarget = form.querySelector(
                        '.targetSelection, .targetSelectionResultWrapper, .troopSelection, .actionButtons')
                        || form;
                      clickTarget.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
                      clickTarget.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
                      clickTarget.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                      return true;
                    }
                    """);
                if (!validationTriggered)
                {
                    Notify($"[farm-list] Could not trigger Add target validation for ({x}|{y}) in '{farmListName}'.");
                    return AddRaidSaveOutcome.Failed;
                }

                Notify($"[farm-list] Add target validation triggered after coordinates for ({x}|{y}) in '{farmListName}'.");
                await DelayFarmListStepAsync(cancellationToken);
            }
            else
            {
                if (troopIndex is null)
                {
                    return AddRaidSaveOutcome.Failed;
                }

                var troopInput = _page.Locator(
                    $"#farmListTargetForm input.unitAmount[name=\"t{troopIndex.Value}\"], " +
                    $"#farmListTargetForm input[name=\"t{troopIndex.Value}\"]").First;
                await troopInput.FillAsync(troopCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await DelayFarmListStepAsync(cancellationToken);
            }
        }

        if (!_config.IsPrivateServer)
        {
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    (lid) => {
                      const form = document.querySelector('#farmListTargetForm');
                      const save = form?.querySelector('button.save, button[type="submit"]');
                      const list = form?.querySelector('select[name="listId"]');
                      const targetError = form?.querySelector(
                        '.targetSelectionResultWrapper.hasError .targetSelectionValidation.show, ' +
                        '.targetSelectionResultWrapper.hasError .customValidationRenderElement');
                      const invalidCoordinates = !!targetError
                        && (targetError.textContent || '').replace(/\s+/g, ' ').trim().length > 0;
                      return invalidCoordinates || (!!save && !save.disabled && (!list || list.value === String(lid)));
                    }
                    """,
                    lid,
                    new PageWaitForFunctionOptions { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
                Notify($"[farm-list] Add target form did not become ready for ({x}|{y}) in '{farmListName}'.");
                return AddRaidSaveOutcome.Failed;
            }
        }

        if (!_config.IsPrivateServer)
        {
            var invalidCoordinates = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const form = document.querySelector('#farmListTargetForm');
                  const targetError = form?.querySelector(
                    '.targetSelectionResultWrapper.hasError .targetSelectionValidation.show, ' +
                    '.targetSelectionResultWrapper.hasError .customValidationRenderElement');
                  return !!targetError
                    && (targetError.textContent || '').replace(/\s+/g, ' ').trim().length > 0;
                }
                """);
            if (invalidCoordinates)
            {
                await _page.EvaluateAsync(
                    """
                    () => {
                      const form = document.querySelector('#farmListTargetForm');
                      const cancel = form?.querySelector('.actionButtons button.cancel')
                        || document.querySelector('.dialogCancelButton');
                      if (cancel) cancel.click();
                    }
                    """);
                try
                {
                    await _page.WaitForFunctionAsync(
                        "() => !document.querySelector('#farmListTargetForm')",
                        null,
                        new PageWaitForFunctionOptions { Timeout = 3000 });
                }
                catch (TimeoutException)
                {
                    Notify($"[farm-list] Invalid Add target dialog remained open after skipping ({x}|{y}).");
                }

                return AddRaidSaveOutcome.InvalidCoordinates;
            }
        }

        await DelayFarmListStepAsync(cancellationToken);
        var clicked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const form = document.querySelector('#farmListTargetForm') || document;
              const save = form.querySelector('button.save, button[type="submit"]');
              if (!save || save.disabled) return false;
              save.click();
              return true;
            }
            """);
        if (!clicked)
        {
            Notify($"[farm-list] Add target Save button was unavailable for ({x}|{y}) in '{farmListName}'.");
            return AddRaidSaveOutcome.Failed;
        }

        if (!_config.IsPrivateServer)
        {
            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const body = (document.body?.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      if (body.includes('already in the selected farm list')) return true;
                      return !document.querySelector('#farmListTargetForm');
                    }
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
                Notify($"[farm-list] Add target save did not finish for ({x}|{y}).");
                return AddRaidSaveOutcome.Failed;
            }
        }
        else
        {
            await Task.Delay(350, cancellationToken);
        }

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
        long troopCount,
        int troopVariancePercent,
        int x,
        int y,
        bool raidAttack,
        CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before filling manual farming form.", cancellationToken);
        var randomizedTroopCount = ResolveRandomizedTroopCount(troopCount, troopVariancePercent);
        var minimumAcceptedTroopCount = Math.Max(1L, (long)Math.Ceiling(randomizedTroopCount * ManualFarmingMinimumTroopRatio));
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
            troopCountToSend = Math.Min(randomizedTroopCount, Math.Max(0L, availableTroopCount.Value));
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
                availableTroopCount ?? 0L,
                0,
                minimumAcceptedTroopCount);
        }

        var attackModeSelected = await TrySelectAttackModeAsync(raidAttack, cancellationToken);
        if (!attackModeSelected)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0L,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        var firstConfirmClicked = await TryClickConfirmButtonAsync(cancellationToken);
        if (!firstConfirmClicked)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0L,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        var confirmationPageReady = await WaitForManualAttackConfirmationPageAsync(cancellationToken);
        if (!confirmationPageReady)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0L,
                troopCountToSend,
                minimumAcceptedTroopCount);
        }

        var secondConfirmClicked = await TryClickConfirmButtonAsync(cancellationToken);

        if (!secondConfirmClicked)
        {
            return new ManualAttackSendResult(
                ManualAttackSendStatus.Failed,
                availableTroopCount ?? 0L,
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

    private async Task<long?> ReadAvailableTroopCountAsync(string fieldToken, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading available troops.", cancellationToken);
        try
        {
            var result = await _page.EvaluateAsync<long?>(
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

                  const scopeText = input.closest('td, tr, div')?.textContent || '';
                  const scopeMatch = scopeText.match(/\(([\d\s.,]+)\)/);
                  if (scopeMatch) {
                    const parsed = Number.parseInt(scopeMatch[1].replace(/[^\d]/g, ''), 10);
                    if (Number.isFinite(parsed)) return parsed;
                  }

                  // Official Travian (T4.6): the available count is shown after the input as
                  // "<input> / <span>123</span>" inside the same cell (span.none means zero).
                  for (let sib = input.nextElementSibling; sib; sib = sib.nextElementSibling) {
                    if (sib.tagName === 'SPAN') {
                      const n = Number.parseInt((sib.textContent || '').replace(/[^\d]/g, ''), 10);
                      if (Number.isFinite(n)) return n;
                    }
                  }

                  if (input.disabled || input.getAttribute('disabled') !== null) return 0;

                  const maxValue = input.getAttribute('max') || '';
                  const maxParsed = Number.parseInt((maxValue || '').replace(/[^\d]/g, ''), 10);
                  if (Number.isFinite(maxParsed) && maxParsed > 0) return maxParsed;

                  return null;
                }
                """,
                fieldToken);

            return result;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify("Page navigated while reading available troops. Continuing without exact availability.");
            return null;
        }
    }

    private async Task<long?> WaitForAvailableTroopsAsync(string fieldToken, string troopType, CancellationToken cancellationToken)
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

            // The rally point page goes stale while we wait, so the next ReadAvailableTroopCountAsync
            // would re-read the old (zero) troop count. Reload so returning troops are picked up.
            try
            {
                await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                Notify("Page navigated while refreshing troop availability. Continuing.");
            }
        }

        return 0;
    }

    private async Task<bool> TryFillTroopInputAsync(string fieldToken, string troopType, long troopCountToSend, CancellationToken cancellationToken)
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

            var isDisabled = await locator.EvaluateAsync<bool>("node => !!node.disabled || !!node.readOnly || node.getAttribute('disabled') !== null");
            if (isDisabled)
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
                if (node.disabled || node.readOnly || node.getAttribute('disabled') !== null) return false;
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
                  // SS uses radio name="c"; official Travian (T4.6) uses name="eventType".
                  // Attack (3) and raid (4) values are identical on both.
                  const radioButtons = Array.from(document.querySelectorAll('input[type="radio"][name="c"], input[type="radio"][name="eventType"]'));
                  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const radio = radioButtons.find(node => {
                    const value = (node.getAttribute('value') || '').trim();
                    const label = normalize(node.parentElement?.textContent || node.closest('label')?.textContent || '');
                    if (raidAttack) return value === '4' || label.includes('raid');
                    return value === '3' || label.includes('normal attack');
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
                        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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
        InvalidCoordinates = 3,
    }

    private sealed record ManualAttackSendResult(
        ManualAttackSendStatus Status,
        long AvailableTroopCount,
        long SentTroopCount,
        long MinimumAcceptedTroopCount);

    private enum ManualAttackSendStatus
    {
        Failed = 0,
        SkippedLowTroops = 1,
        Sent = 2,
        StoppedByNoTroopsAlarm = 3,
    }

    private static string FormatLargeCount(long value)
    {
        return Math.Max(0, value).ToString("#,0", CultureInfo.InvariantCulture);
    }

    private static int? ClampLongToInt32(long? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value switch
        {
            < int.MinValue => int.MinValue,
            > int.MaxValue => int.MaxValue,
            _ => (int)value.Value,
        };
    }

    private static long ResolveRandomizedTroopCount(long troopCount, int troopVariancePercent)
    {
        var normalizedTroopCount = Math.Max(1L, troopCount);
        var normalizedVariancePercent = troopVariancePercent switch
        {
            0 or 5 or 10 or 20 or 50 => troopVariancePercent,
            _ => 10,
        };

        if (normalizedVariancePercent <= 0)
        {
            return normalizedTroopCount;
        }

        var min = Math.Max(1L, (long)Math.Floor(normalizedTroopCount * (100d - normalizedVariancePercent) / 100d));
        var max = Math.Max(min, (long)Math.Ceiling(normalizedTroopCount * (100d + normalizedVariancePercent) / 100d));
        return Random.Shared.NextInt64(min, max + 1);
    }

    private async Task ClickDetectedUpgradeCandidateAsync(int slotId, int? candidateIndex, CancellationToken cancellationToken)
    {
        if (candidateIndex is null || candidateIndex < 0)
        {
            throw new InvalidOperationException($"Upgrade candidate index is missing for slot {slotId}.");
        }

        await EnsureExpectedBuildSlotPageAsync(slotId, "click detected upgrade candidate", cancellationToken);

        if (await TryClickOfficialPrimaryUpgradeButtonAsync(cancellationToken))
        {
            return;
        }

        var locator = _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(candidateIndex.Value);
        await RetryAsync($"click detected upgrade candidate index {candidateIndex.Value} for slot {slotId}", async () =>
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
        }, cancellationToken: cancellationToken);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking detected upgrade candidate.", cancellationToken);
    }

    private async Task<bool> TryClickOfficialPrimaryUpgradeButtonAsync(CancellationToken cancellationToken)
    {
        var pattern = new Regex(@"upgrade\s+to\s+level", RegexOptions.IgnoreCase);
        var selectors = new[]
        {
            ".upgradeButtonsContainer .section1 button.green.build",
            ".upgradeButtonsContainer .section1 button.green",
            ".upgradeButtonsContainer .section1 button",
        };

        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).Filter(new LocatorFilterOptions { HasTextRegex = pattern }).First;
            if (await locator.CountAsync() <= 0)
            {
                continue;
            }

            try
            {
                await RetryAsync($"click official primary upgrade button '{selector}'", async () =>
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking official upgrade button.", cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                Notify($"Could not click official primary upgrade button '{selector}': {ex.Message}");
            }
        }

        return false;
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
            .Select(TravianParsing.ParseDurationToSeconds)
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

    // Reads the population increase the current build/upgrade page will grant, from the
    // ".culturePointsAndPopulation" panel: the ".unit" whose icon is "population_medium" carries
    // a ".delta" like "(+3)". Returns null when the panel/value is absent (e.g. at max level).
    private async Task<int?> ReadUpgradePopulationDeltaOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? deltaText;
        try
        {
            deltaText = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const units = document.querySelectorAll('.culturePointsAndPopulation .unit, .buildingBenefits .unit');
                  for (const unit of units) {
                    if (unit.querySelector('i.population_medium')) {
                      const delta = unit.querySelector('.delta');
                      return delta ? delta.textContent : null;
                    }
                  }
                  return null;
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[ReadUpgradePopulationDeltaOnCurrentPageAsync] read failed: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(deltaText))
        {
            return null;
        }

        // Strip everything but sign and digits (the markup carries invisible bidi marks).
        var cleaned = new string(deltaText.Where(c => char.IsDigit(c) || c == '-').ToArray());
        if (string.IsNullOrEmpty(cleaned) || cleaned == "-")
        {
            return null;
        }

        return int.TryParse(cleaned, out var value) ? value : null;
    }

    // Adds a population delta to the active village in the cache so the UI village list reflects a
    // just-queued upgrade. The incremental add needs an existing baseline; if it is missing, skip the
    // cache update instead of navigating away from the build flow just to seed UI-only data.
    private async Task AddPopulationToActiveVillageCacheAsync(int delta, CancellationToken cancellationToken)
    {
        if (delta == 0 || _cachedVillages is not { Count: > 0 })
        {
            return;
        }

        string activeName;
        try
        {
            activeName = await ReadActiveVillageNameAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Notify($"[AddPopulationToActiveVillageCacheAsync] could not read active village: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(activeName))
        {
            return;
        }

        var hasBaseline = _cachedVillages.Any(v =>
            string.Equals(v.Name, activeName, StringComparison.Ordinal) && v.Population.HasValue);
        if (!hasBaseline)
        {
            Notify($"[population] no baseline for '{activeName}', skipping UI cache population delta to avoid extra navigation.");
            return;
        }

        var updated = false;
        var list = _cachedVillages.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, activeName, StringComparison.Ordinal)
                && list[i].Population is int currentPop)
            {
                list[i] = list[i] with { Population = currentPop + delta };
                updated = true;
                break;
            }
        }

        if (updated)
        {
            _cachedVillages = list;
            Notify($"[population] active village '{activeName}' population +{delta} (UI cache updated).");
            // Push the new population to the UI immediately (bypass the 20s ui-sync throttle) so the
            // "Villages" box reflects each upgrade as it completes. Cheap: reads the current page only.
            await TryEmitUiSyncSnapshotAsync(cancellationToken, force: true);
        }
    }

    private async Task CaptureFailureArtifactsAsync(string label, CancellationToken cancellationToken)
    {
        if (_page.IsClosed)
        {
            return;
        }

        var safeLabel = TravianUrls.SafePathSegment(label);
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

    private sealed class FarmListRowJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("activeFarmCount")]
        public int? ActiveFarmCount { get; init; }

        [JsonPropertyName("totalFarmCount")]
        public int? TotalFarmCount { get; init; }

        [JsonPropertyName("capacity")]
        public int? Capacity { get; init; }

        [JsonPropertyName("farmCoordinates")]
        public string[]? FarmCoordinates { get; init; }

        [JsonPropertyName("timerText")]
        public string? TimerText { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }

        [JsonPropertyName("lid")]
        public string? Lid { get; init; }
    }

    private sealed class FarmDispatchLimitStateJs
    {
        [JsonPropertyName("hasLimit")]
        public bool HasLimit { get; init; }

        [JsonPropertyName("minTimerSeconds")]
        public int? MinTimerSeconds { get; init; }
    }

    private sealed class FarmListSendAllClickStateJs
    {
        [JsonPropertyName("clicked")]
        public bool Clicked { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("listIds")]
        public string[]? ListIds { get; init; }

        [JsonPropertyName("listCount")]
        public int ListCount { get; init; }
    }

    private sealed class FarmListLossRowJs
    {
        [JsonPropertyName("rowIndex")]
        public int RowIndex { get; init; }

        [JsonPropertyName("slotId")]
        public string? SlotId { get; init; }

        [JsonPropertyName("listName")]
        public string? ListName { get; init; }

        [JsonPropertyName("targetName")]
        public string? TargetName { get; init; }

        [JsonPropertyName("rowClass")]
        public string? RowClass { get; init; }

        [JsonPropertyName("raidClass")]
        public string? RaidClass { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }
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

    private sealed class ActiveVillageCoordJs
    {
        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }
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

        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }

        // Official only: population of the active village, read straight from the sidebar
        // (div.population > span). Null for non-active rows and when the cell is absent.
        [JsonPropertyName("population")]
        public int? Population { get; init; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; init; }
    }
}
