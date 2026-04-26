using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private readonly IPage _page;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool _interactive;
    private readonly bool _browserVisible;
    private readonly string _projectRoot;
    private readonly string _capitalCachePath;
    private readonly Action<string>? _statusCallback;
    private DateTimeOffset? _serverTimeUtc;

    // Session-level cache for the villages list. Spieler.php is expensive to load and the data
    // changes rarely, so we share one read across LoginAsync, SwitchToVillageAsync and status reads.
    private static readonly TimeSpan VillagesCacheTtl = TimeSpan.FromSeconds(60);
    private List<Village>? _cachedVillages;
    private DateTimeOffset _cachedVillagesAt = DateTimeOffset.MinValue;

    public TravianClient(
        IPage page,
        BotOptions config,
        AccountOptions account,
        bool interactive = true,
        bool browserVisible = true,
        string? projectRoot = null,
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
        _statusCallback = statusCallback;
    }

    public string AccountName => _account.Name;
    public string ServerUrl => _config.BaseUrl.TrimEnd('/');

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before login.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            return;
        }

        Notify("LoginAsync started");

        var loggedInFromCurrentPage = await TryLoginUsingCurrentPageAsync(cancellationToken);
        if (loggedInFromCurrentPage)
        {
            await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
            return;
        }

        await GotoAsync(_config.LoginPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on the login page.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
            return;
        }

        await FillFirstAvailableAsync(Selectors.LoginUsernameField, _account.Username, cancellationToken);
        await FillFirstAvailableAsync(Selectors.LoginPasswordField, _account.Password, cancellationToken);

        if (await CaptchaOrManualStepVisibleAsync())
        {
            Notify("Captcha or manual login step detected.");
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

        await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
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
            Notify("Captcha or manual login step detected.");
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
        Notify("LogoutAsync started");
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

    public async Task<AccountSnapshot> AnalyzeProfileAsync(CancellationToken cancellationToken = default)
    {
        Notify("AnalyzeProfileAsync started");
        await EnsureLoggedInAsync();
        await RefreshCapitalStatesFromPlayerProfileAsync(cancellationToken);
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading account info after profile analysis.", cancellationToken);
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
        await EnsureLoggedInAsync();
        return await ReadGoldClubEnabledAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(CancellationToken cancellationToken = default)
    {
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

        var saved = await TryFillAddRaidFormAndSaveAsync(
            farmListName,
            troopType.Trim(),
            troopCount,
            coordinate.X.Value,
            coordinate.Y.Value,
            cancellationToken);
        if (!saved)
        {
            throw new InvalidOperationException("Could not fill Add Raid form or click Save.");
        }

        await Task.Delay(350, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after saving new raid.", cancellationToken);
        Notify($"Added 1 farm to '{farmListName}' at ({coordinate.X}|{coordinate.Y}) with {troopCount} {troopType}.");
        return new FarmAddResult(farmListName, coordinate.X.Value, coordinate.Y.Value, troopType.Trim(), troopCount);
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

    public async Task SwitchToVillageAsync(string villageName = "", string? villageUrl = null, CancellationToken cancellationToken = default)
    {
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
        });
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

    private async Task EnsureLoggedInAsync()
    {
        if (!await IsLoggedInAsync())
        {
            throw new InvalidOperationException($"Not logged in. Current page state is '{await LoginStateAsync()}'.");
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
                return "logged_out";
            }

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
                    return "logged_out";
                }
            }

            return "unknown";
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
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
            });
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
            });
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
                });
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
            () => {
              const selectors = [
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
                "[id*='captcha' i]"
              ];

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
            """);
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
        return villages;
    }

    private void InvalidateVillagesCache() => _cachedVillagesAt = DateTimeOffset.MinValue;

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

              const selectors = [
                'img.nationBig[alt]',
                'img[src*="/tribes/"][alt]',
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
                const directAlt = element.getAttribute('alt');
                if (directAlt && directAlt.trim()) return directAlt.trim();
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
        var value = await _page.EvaluateAsync<bool>(
            """
            () => {
              const buildButton = document.querySelector('button#buttonBuild');
              if (buildButton) {
                const cls = (buildButton.className || '').toLowerCase();
                if (cls.includes('buildoff') || cls.includes('green')) {
                  return true;
                }
              }

              const candidates = [
                'button#buttonBuild',
                'a[href*="tt=99"]',
                'a[href*="farmlist"]',
                'a[href*="farmList"]',
                '[data-tab*="farm"]',
                '.farmList',
                '.farmlist'
              ];

              for (const selector of candidates) {
                const node = document.querySelector(selector);
                if (!node) continue;
                const text = (node.textContent || '').toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const href = (node.getAttribute('href') || '').toLowerCase();
                if (text.includes('farm') || cls.includes('farm') || href.includes('tt=99')) {
                  return true;
                }
              }

              const body = (document.body?.innerText || '').toLowerCase();
              return /\bfarm\s*list\b/.test(body) || /\bfarmlista\b/.test(body) || /\bfarmliste\b/.test(body);
            }
            """);

        return value;
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
                  total = 500;
                }

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
                ActiveFarmCount: Math.Max(0, row.ActiveFarmCount ?? 0),
                TotalFarmCount: Math.Max(0, row.TotalFarmCount ?? 0),
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

    private async Task<NatarCoordinateJs[]> ReadNatarFarmCoordinatesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading Natar coordinates.", cancellationToken);
        return await _page.EvaluateAsync<NatarCoordinateJs[]>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
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
                const rowText = clean(row.textContent || '');
                const parsed = parsePair(rowText);
                if (!parsed) continue;
                const key = `${parsed.x}|${parsed.y}`;
                if (seen.has(key)) continue;
                seen.add(key);
                rows.push({ x: parsed.x, y: parsed.y });
              }

              if (rows.length > 0) return rows.slice(0, 100);

              for (const anchor of document.querySelectorAll('a[href*="karte.php"]')) {
                const parsed = parsePair(anchor.textContent || '');
                if (!parsed) continue;
                const key = `${parsed.x}|${parsed.y}`;
                if (seen.has(key)) continue;
                seen.add(key);
                rows.push({ x: parsed.x, y: parsed.y });
              }

              return rows.slice(0, 100);
            }
            """);
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

    private async Task<bool> TryFillAddRaidFormAndSaveAsync(
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

        return saved;
    }

    private async Task ClickDetectedUpgradeCandidateAsync(int slotId, int? candidateIndex, CancellationToken cancellationToken)
    {
        if (candidateIndex is null || candidateIndex < 0)
        {
            throw new InvalidOperationException($"Upgrade candidate index is missing for slot {slotId}.");
        }

        await EnsureExpectedBuildSlotPageAsync(slotId, "click detected upgrade candidate");

        var locator = _page.Locator("button, input[type='submit'], input[type='button'], a").Nth(candidateIndex.Value);
        await RetryAsync($"click detected upgrade candidate index {candidateIndex.Value} for slot {slotId}", async () =>
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
        });

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
        _statusCallback?.Invoke(message);
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
}
