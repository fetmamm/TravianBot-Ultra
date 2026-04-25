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
    private static readonly Dictionary<int, int> BuildingMaxLevelsByGid = new()
    {
        [10] = 20, [11] = 20, [15] = 20, [16] = 20, [17] = 20, [18] = 20, [19] = 20, [20] = 20, [21] = 20,
        [22] = 20, [23] = 10, [24] = 20, [25] = 20, [26] = 20, [27] = 20, [28] = 20, [29] = 20, [30] = 20,
        [31] = 20, [32] = 20, [33] = 20, [34] = 20, [35] = 20, [36] = 20, [37] = 20, [38] = 20, [39] = 20,
        [41] = 20, [42] = 20, [43] = 20, [44] = 20,
    };

    private static readonly Dictionary<int, List<(string name, int level)>> BuildingRequirements = new()
    {
        [17] = [("Main Building", 3), ("Warehouse", 1), ("Granary", 1)],
        [18] = [("Main Building", 1)],
        [19] = [("Main Building", 3), ("Rally Point", 1)],
        [20] = [("Academy", 5), ("Blacksmith", 3)],
        [21] = [("Academy", 10), ("Main Building", 5)],
        [22] = [("Barracks", 3), ("Main Building", 3)],
        [24] = [("Academy", 10), ("Main Building", 10)],
        [25] = [("Main Building", 5)],
        [26] = [("Embassy", 1), ("Main Building", 5)],
        [27] = [("Main Building", 10)],
        [28] = [("Marketplace", 20), ("Stable", 10)],
        [31] = [("Rally Point", 1)],
        [32] = [("Rally Point", 1)],
        [33] = [("Rally Point", 1)],
        [34] = [("Main Building", 5)],
        [37] = [("Main Building", 3), ("Rally Point", 1)],
        [41] = [("Stable", 20)],
        [42] = [("Rally Point", 1)],
        [43] = [("Rally Point", 1)],
    };

    private readonly IPage _page;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool _interactive;
    private readonly bool _browserVisible;
    private readonly string _projectRoot;
    private readonly string _capitalCachePath;
    private readonly Action<string>? _statusCallback;
    private readonly object _capitalCacheSync = new();
    private readonly Dictionary<string, CapitalCacheEntry> _capitalCacheByKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _capitalCacheLoaded;
    private DateTimeOffset? _serverTimeUtc;
    private static readonly JsonSerializerOptions CapitalCacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

        await FillFirstAvailableAsync(new[]
        {
            "input[name='name']",
            "input[name='username']",
            "input[name='user']",
            "input[name='login']",
            "input[type='email']",
            "input[type='text']",
        }, _account.Username, cancellationToken);

        await FillFirstAvailableAsync(new[]
        {
            "input[type='password']",
            "input[name='password']",
        }, _account.Password, cancellationToken);

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
        var hasUsernameField = await HasAnySelectorAsync(new[]
        {
            "input[name='name']",
            "input[name='username']",
            "input[name='user']",
            "input[name='login']",
            "input[type='email']",
            "input[type='text']",
        });

        var hasPasswordField = await HasAnySelectorAsync(new[]
        {
            "input[type='password']",
            "input[name='password']",
        });

        if (!hasUsernameField || !hasPasswordField)
        {
            return false;
        }

        Notify("Login form detected on current page. Trying login here first.");

        await FillFirstAvailableAsync(new[]
        {
            "input[name='name']",
            "input[name='username']",
            "input[name='user']",
            "input[name='login']",
            "input[type='email']",
            "input[type='text']",
        }, _account.Username, cancellationToken);

        await FillFirstAvailableAsync(new[]
        {
            "input[type='password']",
            "input[name='password']",
        }, _account.Password, cancellationToken);

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

        var clicked = await TryClickFirstAsync(new[]
        {
            "a[href*='logout']",
            "button[name*='logout' i]",
            "input[name*='logout' i]",
            "form[action*='logout'] button[type='submit']",
            "form[action*='logout'] input[type='submit']",
            "a:has-text('Logout')",
            "a:has-text('Log out')",
            "button:has-text('Logout')",
            "button:has-text('Log out')",
        }, cancellationToken);

        if (!clicked)
        {
            foreach (var candidatePath in new[] { "/logout.php", "/?action=logout", "/index.php?logout=1" })
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

    public async Task<VillageStatus> ReadVillageResourceStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadVillageResourceStatusAsync started");
        if (!IsCurrentUrlForPath("/dorf1.php"))
        {
            await GotoAsync("/dorf1.php", cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening resource fields.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        return await ReadCurrentVillageResourceStatusAsync(cancellationToken);
    }

    public async Task<InboxStatus> ReadInboxStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadInboxStatusAsync started");
        await EnsureLoggedInAsync();
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        return new InboxStatus(
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports);
    }

    public async Task NavigateToResourceFieldsAsync(CancellationToken cancellationToken = default)
    {
        Notify("NavigateToResourceFieldsAsync started");
        if (!IsCurrentUrlForPath("/dorf1.php"))
        {
            await GotoAsync("/dorf1.php", cancellationToken);
        }

        await EnsureLoggedInAsync();
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

        await GotoAsync($"/build.php?id=39&t=99&action=showSlot&lid={lid}", cancellationToken);
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

    public async Task<string> DemolishBuildingToLevelAsync(
        string targetBuildingSlotOrName,
        int targetLevel,
        CancellationToken cancellationToken = default)
    {
        Notify("DemolishBuildingToLevelAsync started");
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Demolish target level must be >= 0.");
        }

        await GotoAsync("/dorf2.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening buildings.", cancellationToken);
        await EnsureLoggedInAsync();

        var status = await ReadCurrentVillageStatusAsync(cancellationToken);
        var mainBuilding = status.Buildings
            .Where(building => SameBuildingName(building.Name, "Main Building"))
            .OrderByDescending(building => building.Level ?? 0)
            .FirstOrDefault();
        if (mainBuilding is null || (mainBuilding.Level ?? 0) < 10)
        {
            throw new InvalidOperationException("Demolition requires Main Building level 10.");
        }

        var target = ResolveTargetBuilding(status, targetBuildingSlotOrName);
        if (target is null || target.SlotId is null || target.Level is null || target.Level <= 0)
        {
            throw new InvalidOperationException($"Could not find a demolishable building for '{targetBuildingSlotOrName}'.");
        }

        if (target.Level <= targetLevel)
        {
            return $"Demolition target already reached for slot {target.SlotId} ({target.Name} level {target.Level}).";
        }

        var started = await TryStartDemolitionStepAsync(
            mainBuildingSlotId: mainBuilding.SlotId ?? 15,
            targetSlotId: target.SlotId.Value,
            targetBuildingName: target.Name,
            cancellationToken);

        if (!started)
        {
            return $"Could not start demolition for {target.Name} in slot {target.SlotId}. Main building page did not expose a standard demolish action.";
        }

        return $"Started demolition for {target.Name} in slot {target.SlotId}. Current level {target.Level}, target level {targetLevel}.";
    }

    public async Task<string> ManageHeroAsync(
        int minHpForAdventure,
        bool autoRevive,
        string statPriority,
        CancellationToken cancellationToken = default)
    {
        Notify("ManageHeroAsync started");
        await GotoAsync("/hero.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening hero page.", cancellationToken);
        await EnsureLoggedInAsync();

        var status = await ReadHeroStatusAsync(cancellationToken);
        if (!status.Exists)
        {
            return "Hero page is unavailable for this account.";
        }

        var actions = new List<string>();
        if (status.IsDead && autoRevive)
        {
            var revived = await TryReviveHeroAsync(cancellationToken);
            actions.Add(revived ? "revive_started" : "revive_not_available");
            status = await ReadHeroStatusAsync(cancellationToken);
        }

        if (status.UnassignedPoints > 0)
        {
            var allocated = await TryAllocateHeroPointsAsync(status.UnassignedPoints, statPriority, cancellationToken);
            if (allocated > 0)
            {
                actions.Add($"points_allocated={allocated}");
            }
        }

        var canSendByHp = !status.IsDead && (status.HpPercent ?? 0) >= Math.Clamp(minHpForAdventure, 1, 100);
        if (status.AdventuresAvailable > 0 && canSendByHp)
        {
            var sent = await TrySendHeroToAdventureAsync(cancellationToken);
            actions.Add(sent ? "adventure_sent" : "adventure_not_clickable");
        }

        status = await ReadHeroStatusAsync(cancellationToken);
        var summary = $"Hero status: dead={status.IsDead}, hp={status.HpPercent?.ToString() ?? "?"}%, adventures={status.AdventuresAvailable}, points={status.UnassignedPoints}";
        if (actions.Count == 0)
        {
            return $"{summary}. No hero action was needed.";
        }

        return $"{summary}. Actions: {string.Join(", ", actions)}.";
    }

    public async Task<bool> MarkMessagesAsReadAsync(CancellationToken cancellationToken = default)
    {
        Notify("MarkMessagesAsReadAsync started");
        await EnsureLoggedInAsync();
        await GotoAsync("/nachrichten.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening messages.", cancellationToken);
        return await TryMarkInboxItemsAsReadAsync(
            markSelectors:
            [
                "button",
                "a",
                ".markAllRead",
                "button[title*='read' i]",
                "a[title*='read' i]",
            ],
            unreadSelectorHints:
            [
                "a[href*='nachrichten' i]",
                "a[href*='message' i]",
                "#n6",
            ],
            label: "messages",
            cancellationToken: cancellationToken);
    }

    public async Task<bool> MarkReportsAsReadAsync(CancellationToken cancellationToken = default)
    {
        Notify("MarkReportsAsReadAsync started");
        await EnsureLoggedInAsync();
        await GotoAsync("/berichte.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening reports.", cancellationToken);
        return await TryMarkInboxItemsAsReadAsync(
            markSelectors:
            [
                "button",
                "a",
                "button[title*='read' i]",
                "a[title*='read' i]",
                ".markAllRead",
            ],
            unreadSelectorHints:
            [
                "a[href*='berichte' i]",
                "a[href*='report' i]",
                "#n5",
            ],
            label: "reports",
            cancellationToken: cancellationToken);
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

    public async Task<string> UpgradeResourceToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default)
    {
        Notify("UpgradeResourceToLevelAsync started");
        if (slotId < 1 || slotId > 18)
        {
            throw new InvalidOperationException($"Resource slot {slotId} is outside the resource field range.");
        }

        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Target level must be 0 or higher.");
        }

        var upgrades = 0;
        var transientRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
                var field = snapshot.ResourceFields.FirstOrDefault(item => item.SlotId == slotId);
                var currentLevel = field?.Level;
                if (currentLevel is null)
                {
                    throw new InvalidOperationException($"Could not read level for resource slot {slotId}.");
                }

                if (currentLevel >= targetLevel)
                {
                    return $"Resource slot {slotId} is level {currentLevel}. Target {targetLevel} reached after {upgrades} upgrades.";
                }

                var queueFingerprintBefore = BuildQueueFingerprint(snapshot.BuildQueue);
                var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
                var detectedMax = actionability.DetectedMaxLevel;
                var effectiveTarget = detectedMax is int maxLevel ? Math.Min(targetLevel, maxLevel) : targetLevel;
                Notify($"Resource slot {slotId}: level={currentLevel}, target={effectiveTarget}, max={detectedMax}, outcome={actionability.Outcome}.");

                if (currentLevel >= effectiveTarget)
                {
                    return $"Resource slot {slotId} is level {currentLevel}. Target {effectiveTarget} reached after {upgrades} upgrades.";
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByMaxLevel)
                {
                    var resolvedMax = detectedMax ?? currentLevel.Value;
                    return $"Resource slot {slotId} appears maxed at level {resolvedMax}. No upgrade performed.";
                }

                if (actionability.Outcome != UpgradeAttemptOutcome.CanUpgrade)
                {
                    return $"Resource slot {slotId} blocked ({actionability.Outcome}): {actionability.Reason}";
                }

                var expectedWaitSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken);
                await ClickDetectedUpgradeCandidateAsync(slotId, actionability.CandidateIndex, cancellationToken);
                await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                upgrades += 1;
                var progress = await WaitForResourceLevelAdvanceAsync(
                    slotId,
                    currentLevel.Value,
                    queueFingerprintBefore,
                    expectedWaitSeconds,
                    cancellationToken);
                if (progress.Advanced)
                {
                    transientRetries = 0;
                    continue;
                }

                if (progress.QueuedOrInProgress)
                {
                    return $"Resource slot {slotId} blocked (QueuedOrInProgress): Upgrade triggered but no immediate level increase; evidence={progress.Evidence} queue_wait_seconds=15";
                }

                return $"Resource slot {slotId} blocked (NoImmediateProgress): Upgrade triggered but level is still {currentLevel} and no queue/in-progress evidence was detected queue_wait_seconds=6";
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex) && transientRetries < 6)
            {
                transientRetries += 1;
                Notify($"UpgradeResourceToLevelAsync transient navigation context error at slot {slotId} ({transientRetries}/6). Retrying...");
                await Task.Delay(250 * transientRetries, cancellationToken);
            }
        }
    }

    public async Task<string> UpgradeResourceToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default)
    {
        Notify("UpgradeResourceToMaxAsync started");
        if (slotId < 1 || slotId > 18)
        {
            throw new InvalidOperationException($"Resource slot {slotId} is outside the resource field range.");
        }

        var upgrades = 0;
        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
            var field = snapshot.ResourceFields.FirstOrDefault(item => item.SlotId == slotId);
            var currentLevel = field?.Level;
            if (currentLevel is null)
            {
                throw new InvalidOperationException($"Could not read level for resource slot {slotId}.");
            }

            var queueFingerprintBefore = BuildQueueFingerprint(snapshot.BuildQueue);
            var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
            var detectedMax = actionability.DetectedMaxLevel;
            Notify($"Resource max loop slot {slotId}: level={currentLevel}, detectedMax={detectedMax}, outcome={actionability.Outcome}.");

            if ((detectedMax is int maxLevel && currentLevel >= maxLevel) || actionability.Outcome == UpgradeAttemptOutcome.BlockedByMaxLevel)
            {
                var resolvedMax = detectedMax ?? currentLevel.Value;
                return $"Resource slot {slotId} reached max level {resolvedMax}. Upgrades made: {upgrades}.";
            }

            if (actionability.Outcome != UpgradeAttemptOutcome.CanUpgrade)
            {
                return $"Resource slot {slotId} blocked ({actionability.Outcome}): {actionability.Reason}. Upgrades made: {upgrades}.";
            }

            var expectedWaitSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken);
            await ClickDetectedUpgradeCandidateAsync(slotId, actionability.CandidateIndex, cancellationToken);
            await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
            upgrades += 1;
            var progress = await WaitForResourceLevelAdvanceAsync(
                slotId,
                currentLevel.Value,
                queueFingerprintBefore,
                expectedWaitSeconds,
                cancellationToken);
            if (!progress.Advanced)
            {
                if (progress.QueuedOrInProgress)
                {
                    return $"Upgrade triggered for resource slot {slotId}, no immediate level increase, but queue/in-progress evidence was detected ({progress.Evidence}). Upgrades made: {upgrades}.";
                }

                return $"Upgrade triggered for resource slot {slotId}, but no immediate level increase and no queue/in-progress evidence detected. Upgrades made: {upgrades}.";
            }
        }

        return $"Resource slot {slotId} reached max attempt limit after {upgrades} upgrades.";
    }

    public async Task<string> UpgradeAllResourcesToLevelAsync(int targetLevel, CancellationToken cancellationToken = default)
    {
        Notify($"Starting upgrade of all resources to level {targetLevel}.");
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Target level must be 0 or higher.");
        }

        var upgrades = 0;
        var knownLevelsBySlot = new Dictionary<int, int>();
        var transientRetries = 0;
        int? currentTransientSlot = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await GotoAsync("/dorf1.php", cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource fields.", cancellationToken);
                await EnsureLoggedInAsync();
                var resourceFields = await ReadResourceFieldsAsync(cancellationToken);
                NotifyResourceLevelIncreases(knownLevelsBySlot, resourceFields);
                knownLevelsBySlot = BuildResourceLevelMap(resourceFields);
                var buildQueue = await ReadBuildQueueAsync(cancellationToken);
                var queueWaitSeconds = ResolveShortestQueueDurationSeconds(buildQueue);

                if (queueWaitSeconds is int waitSeconds && waitSeconds > 0)
                {
                    Notify($"Resource queue busy for about {waitSeconds}s before the next upgrade attempt. queue_wait_seconds={waitSeconds}");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                    transientRetries = 0;
                    continue;
                }

                var fallbackMax = 40;
                var candidates = resourceFields
                    .Where(field => field.SlotId is not null && field.Level is not null)
                    .OrderBy(field => field.Level ?? 0)
                    .ThenBy(field => field.SlotId ?? 999)
                    .ToList();

                var attemptedAny = false;
                var blockReasons = new List<string>();

                foreach (var candidate in candidates)
                {
                    var slot = candidate.SlotId ?? 0;
                    currentTransientSlot = slot;
                    var level = candidate.Level ?? 0;
                    var preliminaryTarget = Math.Min(targetLevel, fallbackMax);

                    if (level >= preliminaryTarget)
                    {
                        continue;
                    }

                    var actionability = await AnalyzeUpgradeActionabilityAsync(slot, cancellationToken, performClick: false);
                    var cap = actionability.DetectedMaxLevel ?? fallbackMax;
                    var effectiveTarget = Math.Min(targetLevel, cap);
                    if (level >= effectiveTarget)
                    {
                        continue;
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.CanUpgrade)
                    {
                        attemptedAny = true;
                        var rawUpgradeSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken);
                        await ClickDetectedUpgradeCandidateAsync(slot, actionability.CandidateIndex, cancellationToken);
                        await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                        upgrades += 1;

                        var upgradeWaitSeconds = ComputeUpgradeWaitSeconds(rawUpgradeSeconds);
                        Notify($"Resource slot {slot} upgrade duration: {FormatDuration(rawUpgradeSeconds ?? 0)}. Waiting {upgradeWaitSeconds}s. queue_wait_seconds={upgradeWaitSeconds}");
                        await Task.Delay(TimeSpan.FromSeconds(upgradeWaitSeconds), cancellationToken);
                        transientRetries = 0;
                        goto NextLoopTick;
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByQueue)
                    {
                        // The top-of-loop build queue read will detect the remaining duration and wait.
                        Notify($"Resource slot {slot} blocked by queue. Retrying after queue clears.");
                        transientRetries = 0;
                        goto NextLoopTick;
                    }

                    blockReasons.Add($"slot {slot}: {actionability.Outcome} ({actionability.Reason})");
                }

                if (!attemptedAny)
                {
                    if (blockReasons.Count == 0)
                    {
                        return $"All resource fields are at or above target level {targetLevel}. Upgrades made: {upgrades}.";
                    }

                    var reasonSuffix = blockReasons.Count > 0 ? $" Blockers: {string.Join(", ", blockReasons)}." : string.Empty;
                    if (!IsCurrentUrlForPath("/dorf1.php"))
                    {
                        await GotoAsync("/dorf1.php", cancellationToken);
                    }

                    return $"No resource slot could be upgraded toward level {targetLevel}. Upgrades made: {upgrades}.{reasonSuffix}";
                }

                transientRetries = 0;
                currentTransientSlot = null;

            NextLoopTick:
                ;
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex) && transientRetries < 8)
            {
                transientRetries += 1;
                if (currentTransientSlot is int slotId)
                {
                    Notify($"Upgrade-all hit transient navigation context error at slot {slotId} ({transientRetries}/8). Retrying...");
                }
                else
                {
                    Notify($"Upgrade-all hit transient navigation context error ({transientRetries}/8). Retrying...");
                }
                await Task.Delay(300 * transientRetries, cancellationToken);
            }
        }
    }

    private static Dictionary<int, int> BuildResourceLevelMap(IReadOnlyList<ResourceField> fields)
    {
        var map = new Dictionary<int, int>();
        foreach (var field in fields)
        {
            if (field.SlotId is not int slotId || field.Level is not int level)
            {
                continue;
            }

            map[slotId] = level;
        }

        return map;
    }

    private void NotifyResourceLevelIncreases(
        IReadOnlyDictionary<int, int> knownLevelsBySlot,
        IReadOnlyList<ResourceField> currentFields)
    {
        foreach (var field in currentFields)
        {
            if (field.SlotId is not int slotId || field.Level is not int currentLevel)
            {
                continue;
            }

            if (!knownLevelsBySlot.TryGetValue(slotId, out var previousLevel))
            {
                continue;
            }

            if (currentLevel > previousLevel)
            {
                Notify($"Resource slot {slotId} level increased from {previousLevel} to {currentLevel}.");
            }
        }
    }

    public async Task<string> UpgradeBuildingToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default)
    {
        Notify("UpgradeBuildingToLevelAsync started");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }

        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Target level must be 0 or higher.");
        }

        var upgrades = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await ReadVillageStatusAsync(cancellationToken);
            var building = status.Buildings.FirstOrDefault(item => item.SlotId == slotId);
            var currentLevel = building?.Level;
            if (currentLevel is null)
            {
                throw new InvalidOperationException($"Could not read level for building slot {slotId}.");
            }

            var maxLevel = await ResolveBuildingMaxLevelAsync(building!, slotId, cancellationToken);
            if (targetLevel > maxLevel)
            {
                throw new InvalidOperationException($"{building!.Name} can only be upgraded to level {maxLevel}. Requested level {targetLevel}.");
            }

            if (currentLevel >= targetLevel)
            {
                return $"Building slot {slotId} is level {currentLevel}. Target {targetLevel} reached after {upgrades} upgrades.";
            }

            EnsureBuildingRequirementsMet(status, building!.Gid, building.Name);
            var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: true);
            Notify($"Building slot {slotId}: level={currentLevel}, max={maxLevel}, outcome={actionability.Outcome}.");
            if (actionability.Outcome != UpgradeAttemptOutcome.CanUpgrade)
            {
                return $"Building slot {slotId} blocked ({actionability.Outcome}): {actionability.Reason}";
            }

            upgrades += 1;
            var queueFingerprintBefore = BuildQueueFingerprint(status.BuildQueue);
            var progress = await WaitForBuildingLevelAdvanceAsync(
                slotId,
                currentLevel.Value,
                queueFingerprintBefore,
                cancellationToken);
            if (progress.Advanced)
            {
                continue;
            }

            if (progress.QueuedOrInProgress)
            {
                return $"Upgrade triggered for building slot {slotId}. No immediate level increase, but queue/in-progress evidence was detected ({progress.Evidence}).";
            }

            return $"Upgrade triggered for building slot {slotId}, but no immediate level increase and no queue/in-progress evidence detected.";
        }
    }

    public async Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default)
    {
        Notify("UpgradeBuildingToMaxAsync started");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }

        var upgrades = 0;
        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await ReadVillageStatusAsync(cancellationToken);
            var building = status.Buildings.FirstOrDefault(item => item.SlotId == slotId);
            if (building is not null && building.Level is not null)
            {
                var maxLevel = await ResolveBuildingMaxLevelAsync(building, slotId, cancellationToken);
                if (building.Level >= maxLevel)
                {
                    return $"Building slot {slotId} is already at max level {maxLevel}. Upgrades made: {upgrades}.";
                }

                EnsureBuildingRequirementsMet(status, building.Gid, building.Name);
            }

            var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: true);
            if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByMaxLevel)
            {
                return $"Building slot {slotId} reports max level reached. Upgrades made: {upgrades}.";
            }

            if (actionability.Outcome != UpgradeAttemptOutcome.CanUpgrade)
            {
                return $"Building slot {slotId} blocked ({actionability.Outcome}): {actionability.Reason}. Upgrades made: {upgrades}.";
            }

            upgrades += 1;
            if (building?.Level is int knownLevel)
            {
                var queueFingerprintBefore = BuildQueueFingerprint(status.BuildQueue);
                var progress = await WaitForBuildingLevelAdvanceAsync(
                    slotId,
                    knownLevel,
                    queueFingerprintBefore,
                    cancellationToken);
                if (!progress.Advanced)
                {
                    if (progress.QueuedOrInProgress)
                    {
                        return $"Upgrade triggered for building slot {slotId}, no immediate level increase, but queue/in-progress evidence was detected ({progress.Evidence}). Upgrades made: {upgrades}.";
                    }

                    return $"Upgrade triggered for building slot {slotId}, but no immediate level increase and no queue/in-progress evidence detected. Upgrades made: {upgrades}.";
                }
            }
        }

        return $"Building slot {slotId} reached max attempt limit after {upgrades} upgrades.";
    }

    public async Task<string> ConstructBuildingAsync(int slotId, int gid, string name, CancellationToken cancellationToken = default)
    {
        Notify("ConstructBuildingAsync started");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }

        if (gid <= 0)
        {
            throw new InvalidOperationException("Building gid must be positive.");
        }

        var buildingName = string.IsNullOrWhiteSpace(name) ? $"gid {gid}" : name.Trim();

        var status = await ReadVillageStatusAsync(cancellationToken);
        EnsureBuildingCanBeConstructed(status, gid, buildingName);

        await GotoAsync($"/build.php?id={slotId}", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the building slot.", cancellationToken);
        await EnsureLoggedInAsync();
        await EnsureExpectedBuildSlotPageAsync(slotId, "construct building");
        await ApplyActionDelayAsync(cancellationToken);
        await EnsureServerAllowsConstructionAsync(slotId, gid, buildingName, cancellationToken);

        bool clicked;
        try
        {
            clicked = await RetryTruthyAsync(
                "click construct building",
                async () => await _page.EvaluateAsync<bool>(
                    """
                    ({ gid }) => {
                      const gidText = String(gid);
                      const candidates = Array.from(document.querySelectorAll('a, button, input[type="submit"]'));
                      for (const element of candidates) {
                        const href = element.getAttribute('href') || '';
                        const value = element.getAttribute('value') || '';
                        const title = element.getAttribute('title') || '';
                        const text = `${element.textContent || ''} ${value} ${title}`.toLowerCase();
                        const form = element.closest('form');
                        const formAction = form ? (form.getAttribute('action') || '') : '';
                        const classes = (element.className || '').toString().toLowerCase();
                        const disabled = element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true';
                        const isGold = classes.includes('gold') || text.includes('npc') || text.includes('instant');
                        const gidMatches =
                          href.includes(`gid=${gidText}`)
                          || href.includes(`gid%3D${gidText}`)
                          || classes.includes(`gid${gidText}`)
                          || formAction.includes(`gid=${gidText}`)
                          || formAction.includes(`gid%3D${gidText}`)
                          || (element.getAttribute('data-gid') || '') === gidText;
                        const inBuildContainer = !!element.closest('.contract, .buildingWrapper, .build_details, .contractLink, .upgradeBuilding, #contract');
                        const looksBuildable =
                          classes.includes('green')
                          || classes.includes('build')
                          || classes.includes('contract')
                          || inBuildContainer;
                        if (!disabled && !isGold && gidMatches && looksBuildable) {
                          element.click();
                          return true;
                        }
                      }
                      return false;
                    }
                    """,
                    new { gid }));
        }
        catch (Exception ex)
        {
            await CaptureFailureArtifactsAsync($"construct-slot-{slotId}-gid-{gid}", cancellationToken);
            throw new InvalidOperationException($"Construct building failed for slot {slotId}, gid {gid}: {ex.Message}", ex);
        }

        if (!clicked)
        {
            await CaptureFailureArtifactsAsync($"construct-slot-{slotId}-gid-{gid}-no-click", cancellationToken);
            return $"{buildingName} could not be built in slot {slotId}. Requirements, resources, or queue may block it.";
        }

        await RetryAsync("wait for page load", async () =>
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        });
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting construction.", cancellationToken);
        await ApplyActionDelayAsync(cancellationToken);
        return $"Started construction of {buildingName} in slot {slotId}.";
    }

    public async Task<IReadOnlyList<ServerBuildChoice>> ReadAvailableBuildingsForSlotAsync(int slotId, CancellationToken cancellationToken = default)
    {
        Notify("ReadAvailableBuildingsForSlotAsync started");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }

        await GotoAsync($"/build.php?id={slotId}", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading build choices.", cancellationToken);
        await EnsureLoggedInAsync();
        return await ReadServerBuildChoicesOnCurrentPageAsync(cancellationToken);
    }

    public async Task SwitchToVillageAsync(string villageName = "", string? villageUrl = null, CancellationToken cancellationToken = default)
    {
        var activeVillageBeforeSwitch = await TryReadActiveVillageNameSafeAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(villageUrl))
        {
            await GotoAsync(villageUrl, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(villageName))
        {
            await GotoAsync(_config.VillageOverviewPath, cancellationToken);
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

    private async Task<VillageStatus> ReadCurrentVillageResourceStatusAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading resource status.", cancellationToken);
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

        var resourceFields = await ReadResourceFieldsAsync(cancellationToken);

        // Fast capital detection: non-capital villages are capped at level 10
        var cachedIsCapital = TryGetCachedCapitalState(activeVillage);
        if (cachedIsCapital != true && resourceFields.Any(f => f.Level > 10))
        {
            Notify($"Fast capital detection: resource field above level 10 found — '{activeVillage}' is capital.");
            SaveCachedVillageState(activeVillage, true, null, null);
            cachedIsCapital = true;
            // Rebuild villages list with updated capital flag
            villages = (await ReadVillagesAsync(cancellationToken)).ToList();
        }

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: villages,
            Resources: resources,
            ResourceFields: resourceFields,
            Buildings: [],
            BuildQueue: buildQueue,
            Tribe: await ReadTribeAsync(cancellationToken),
            VillageCount: villages.Count,
            Gold: currency.Gold,
            Silver: currency.Silver,
            IsBuildingInProgress: buildQueue.Count > 0,
            ActiveBuildCount: buildQueue.Count,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? FormatDuration(left) : string.Empty,
            IsCapital: cachedIsCapital,
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

            var loggedInSelectors = new[]
            {
                "a[href*='logout']",
                "img[alt*='Logout' i]",
                "a[href*='dorf1.php']",
                "a[href*='dorf2.php']",
                "#sidebarBoxVillagelist",
                ".villageList",
                "#villageList",
                "#resourceFieldContainer",
                "#village_map",
            };
            foreach (var selector in loggedInSelectors)
            {
                if (await _page.Locator(selector).CountAsync() > 0)
                {
                    return "logged_in";
                }
            }

            var loggedOutSelectors = new[]
            {
                "input[type='password']",
                "input[name='password']",
                "button[type='submit']",
                "input[type='submit']",
                "a[href*='login']",
            };
            foreach (var selector in loggedOutSelectors)
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
        var selectors = new[]
        {
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Login')",
            "button:has-text('Log in')",
            "a:has-text('Login')",
        };

        foreach (var selector in selectors)
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
        var directContinueLink = _page.Locator("a[href*='dorf1.php?ok']");
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
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading villages.", cancellationToken);
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

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<List<string>>()
            : JsonSerializer.Deserialize<List<List<string>>>(rawJson) ?? new List<List<string>>();

        raw ??= [];
        return raw
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
                    CoordY: cy);
            })
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadResourcesAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resources.", cancellationToken);
        var raw = await _page.EvaluateAsync<Dictionary<string, string>>(
            """
            () => {
              const ids = {
                wood: ['#l1', '#stockBarResource1'],
                clay: ['#l2', '#stockBarResource2'],
                iron: ['#l3', '#stockBarResource3'],
                crop: ['#l4', '#stockBarResource4']
              };
              const resources = {};

              for (const [name, selectors] of Object.entries(ids)) {
                for (const selector of selectors) {
                  const element = document.querySelector(selector);
                  if (!element) continue;
                  const value = (element.textContent || '').replace(/\s+/g, '').trim();
                  if (value) {
                    resources[name] = value;
                    break;
                  }
                }
              }

              return resources;
            }
            """);
        return raw ?? new Dictionary<string, string>();
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

    private async Task<IReadOnlyDictionary<string, double?>> ReadResourceProductionPerHourAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading production rates.", cancellationToken);
        var raw = await _page.EvaluateAsync<Dictionary<string, double?>>(
            """
            () => {
              const parseNumber = (value) => {
                const text = (value || '').replace(/\s+/g, ' ').trim();
                if (!text) return null;
                const match = text.match(/([+-]?\d[\d\s.,']*)/);
                if (!match) return null;
                const cleaned = match[1].replace(/\s+/g, '').replace(/,/g, '.').replace(/[^0-9+.-]/g, '');
                if (!cleaned) return null;
                const parsed = Number(cleaned);
                return Number.isFinite(parsed) ? parsed : null;
              };

              const readFirst = (selectors) => {
                for (const selector of selectors) {
                  for (const node of document.querySelectorAll(selector)) {
                    const value =
                      parseNumber(node.getAttribute('data-value'))
                      ?? parseNumber(node.getAttribute('data-rate'))
                      ?? parseNumber(node.textContent || '')
                      ?? parseNumber(node.getAttribute('title') || '');
                    if (value !== null) return value;
                  }
                }
                return null;
              };

              return {
                wood: readFirst(['#production .wood .num', '#production .wood', '#production .r1 .num', '#production .r1']),
                clay: readFirst(['#production .clay .num', '#production .clay', '#production .r2 .num', '#production .r2']),
                iron: readFirst(['#production .iron .num', '#production .iron', '#production .r3 .num', '#production .r3']),
                crop: readFirst(['#production .crop .num', '#production .crop', '#production .r4 .num', '#production .r4']),
              };
            }
            """);

        return raw ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ResourceStorageForecast> BuildResourceForecasts(
        IReadOnlyDictionary<string, string> resources,
        (int? Warehouse, int? Granary) capacities,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        var result = new List<ResourceStorageForecast>();
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            resources.TryGetValue(key, out var rawCurrent);
            var current = TryParseResourceValue(rawCurrent);
            var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? capacities.Granary
                : capacities.Warehouse;

            productionByHour.TryGetValue(key, out var production);
            double? percent = null;
            if (capacity is > 0 && current is not null)
            {
                percent = Math.Clamp((double)current.Value / capacity.Value * 100.0, 0.0, 100.0);
            }

            int? secondsToFull = null;
            if (capacity is > 0 && current is not null && production is > 0)
            {
                var remaining = Math.Max(0, capacity.Value - current.Value);
                secondsToFull = (int)Math.Ceiling((remaining / production.Value) * 3600.0);
            }

            result.Add(new ResourceStorageForecast(
                ResourceKey: key,
                Current: current,
                Capacity: capacity,
                PercentOfCapacity: percent,
                ProductionPerHour: production,
                SecondsToFull: secondsToFull));
        }

        return result;
    }

    private static int? TryParseResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : null;
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

    private static int? ParseNumericTextToInt(string? value)
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
        await GotoAsync("/build.php?id=39&t=99", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening farmlists.", cancellationToken);
        await EnsureLoggedInAsync();
        if (await IsFarmListPageAsync(cancellationToken))
        {
            return;
        }

        await GotoAsync("/build.php?id=39&fastUP=0", cancellationToken);
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

        await GotoAsync("/build.php?id=39&t=99", cancellationToken);
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
            await GotoAsync("/build.php?id=39&t=99", cancellationToken);
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
        await GotoAsync("/spieler.php?uid=3", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening Natars profile.", cancellationToken);
        await EnsureLoggedInAsync();

        var coordinates = await ReadNatarFarmCoordinatesFromCurrentPageAsync(cancellationToken);
        if (coordinates.Length > 0)
        {
            return coordinates[0];
        }

        await GotoAsync("/statistiken.php?id=100", cancellationToken);
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

    private static Building? ResolveTargetBuilding(VillageStatus status, string targetBuildingSlotOrName)
    {
        if (int.TryParse(targetBuildingSlotOrName.Trim(), out var slotId))
        {
            return status.Buildings.FirstOrDefault(item => item.SlotId == slotId);
        }

        return status.Buildings
            .Where(item => item.Level is > 0)
            .OrderByDescending(item => item.Level ?? 0)
            .FirstOrDefault(item =>
                item.Name.Contains(targetBuildingSlotOrName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> TryStartDemolitionStepAsync(
        int mainBuildingSlotId,
        int targetSlotId,
        string targetBuildingName,
        CancellationToken cancellationToken)
    {
        await GotoAsync($"/build.php?id={mainBuildingSlotId}", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the main building.", cancellationToken);

        var selected = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const slotId = Number(args.slotId);
              const normalized = (args.name || '').toLowerCase();
              const selectCandidates = [
                'select[name*="demolish" i]',
                'form[action*="build.php" i] select',
                '#build.gid15 select',
                '.demolish select',
                '#content select'
              ];

              const getCandidates = () => {
                const nodes = [];
                for (const selector of selectCandidates) {
                  for (const node of document.querySelectorAll(selector)) {
                    if (!nodes.includes(node)) nodes.push(node);
                  }
                }
                return nodes;
              };

              const selects = getCandidates();
              for (const select of selects) {
                const options = Array.from(select.options || []);
                const direct = options.find(option => Number(option.value) === slotId);
                if (direct) {
                  select.value = direct.value;
                  select.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }

                const byText = options.find(option => {
                  const text = (option.textContent || '').toLowerCase();
                  return text.includes(normalized) || text.includes(`(${slotId})`) || text.includes(` ${slotId} `);
                });
                if (byText) {
                  select.value = byText.value;
                  select.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
              }

              return false;
            }
            """,
            new { slotId = targetSlotId, name = targetBuildingName });

        if (!selected)
        {
            return false;
        }

        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const clickables = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
              const safe = clickables.filter(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const id = (node.id || '').toLowerCase();
                const isDemolish = text.includes('demolish') || text.includes('abbrechen') || text.includes('riva') || text.includes('demoliera');
                const isGold = text.includes('gold') || text.includes('instant') || cls.includes('gold') || id.includes('gold');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return isDemolish && !isGold && !disabled;
              });

              if (!safe.length) return false;
              safe[0].click();
              return true;
            }
            """);
    }

    private async Task<HeroStatus> ReadHeroStatusAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading hero status.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
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

              const parseTimer = (value) => {
                const text = (value || '').trim();
                if (!text) return null;
                const parts = text.split(':').map(v => Number(v));
                if (parts.some(v => !Number.isFinite(v))) return null;
                if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
                if (parts.length === 2) return parts[0] * 60 + parts[1];
                return null;
              };

              const text = (document.body?.innerText || '').toLowerCase();
              const dead = /\bdead\b|\btot\b|\bdeceased\b|\bd\u00f6d\b/.test(text);

              const hp =
                parseNumber(document.querySelector('#health')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="health" i]')?.textContent || '')
                ?? parseNumber(document.querySelector('[id*="health" i]')?.textContent || '');

              const adventures =
                parseNumber(document.querySelector('#adventureCount')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="adventure" i] .badge')?.textContent || '')
                ?? parseNumber(document.querySelector('[id*="adventure" i] .badge')?.textContent || '')
                ?? 0;

              const points =
                parseNumber(document.querySelector('#points')?.textContent || '')
                ?? parseNumber(document.querySelector('[class*="attribute" i] [class*="free" i]')?.textContent || '')
                ?? 0;

              const adventureTimer =
                parseTimer(document.querySelector('.adventure .timer')?.textContent || '')
                ?? parseTimer(document.querySelector('[class*="adventure" i] [class*="timer" i]')?.textContent || '');

              const returnTimer =
                parseTimer(document.querySelector('[class*="return" i] [class*="timer" i]')?.textContent || '')
                ?? parseTimer(document.querySelector('.heroReturn .timer')?.textContent || '');

              const exists = !!document.querySelector('#heroImage, #heroStatus, [class*="hero" i]');
              return JSON.stringify({
                exists,
                isDead: dead,
                hpPercent: hp,
                adventuresAvailable: adventures || 0,
                secondsUntilAdventureReady: adventureTimer,
                secondsUntilReturn: returnTimer,
                unassignedPoints: points || 0
              });
            }
            """);

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new HeroStatus();
        }

        var parsed = JsonSerializer.Deserialize<HeroStatusJs>(rawJson);
        if (parsed is null)
        {
            return new HeroStatus();
        }

        return new HeroStatus(
            Exists: parsed.Exists,
            IsDead: parsed.IsDead,
            HpPercent: parsed.HpPercent,
            AdventuresAvailable: parsed.AdventuresAvailable ?? 0,
            SecondsUntilAdventureReady: parsed.SecondsUntilAdventureReady,
            SecondsUntilReturn: parsed.SecondsUntilReturn,
            UnassignedPoints: parsed.UnassignedPoints ?? 0);
    }

    private async Task<bool> TryReviveHeroAsync(CancellationToken cancellationToken)
    {
        await GotoAsync("/hero.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while trying to revive hero.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const buttons = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
              const candidate = buttons.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const isRevive = text.includes('revive') || text.includes('resurrect') || text.includes('återuppliva');
                const isGold = text.includes('gold') || text.includes('instant') || cls.includes('gold');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return isRevive && !isGold && !disabled;
              });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);
    }

    private async Task<int> TryAllocateHeroPointsAsync(int points, string priority, CancellationToken cancellationToken)
    {
        if (points <= 0)
        {
            return 0;
        }

        await GotoAsync("/hero.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while assigning hero points.", cancellationToken);

        var priorities = ParseHeroStatPriority(priority);
        var allocated = 0;
        foreach (var stat in priorities)
        {
            if (allocated >= points)
            {
                break;
            }

            var changed = await _page.EvaluateAsync<bool>(
                """
                (name) => {
                  const matches = Array.from(document.querySelectorAll('input[type="number"], input[type="text"]'))
                    .filter(node => {
                      const id = (node.id || '').toLowerCase();
                      const field = (node.getAttribute('name') || '').toLowerCase();
                      const label = ((node.closest('tr, .row, .attribute, .skill')?.textContent) || '').toLowerCase();
                      return id.includes(name) || field.includes(name) || label.includes(name);
                    });

                  if (!matches.length) return false;
                  const target = matches[0];
                  const current = Number(target.value || '0');
                  if (!Number.isFinite(current)) return false;
                  target.value = String(current + 1);
                  target.dispatchEvent(new Event('input', { bubbles: true }));
                  target.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
                """,
                stat);

            if (changed)
            {
                allocated += 1;
            }
        }

        if (allocated <= 0)
        {
            return 0;
        }

        var saved = await _page.EvaluateAsync<bool>(
            """
            () => {
              const buttons = Array.from(document.querySelectorAll('button, input[type="submit"]'));
              const save = buttons.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const isSave = text.includes('save') || text.includes('apply') || text.includes('ok');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return isSave && !disabled;
              });
              if (!save) return false;
              save.click();
              return true;
            }
            """);

        return saved ? allocated : 0;
    }

    private static IReadOnlyList<string> ParseHeroStatPriority(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ["offense", "resource", "regeneration"];
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["offense"] = "off",
            ["off"] = "off",
            ["attack"] = "off",
            ["resource"] = "resource",
            ["production"] = "resource",
            ["regen"] = "regen",
            ["regeneration"] = "regen",
            ["health"] = "health",
            ["defense"] = "def",
            ["def"] = "def",
        };

        var parsed = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => map.GetValueOrDefault(item, item.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parsed.Count == 0
            ? ["offense", "resource", "regeneration"]
            : parsed;
    }

    private async Task<bool> TrySendHeroToAdventureAsync(CancellationToken cancellationToken)
    {
        await GotoAsync("/hero.php?t=3", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening adventures.", cancellationToken);
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const buttons = Array.from(document.querySelectorAll('button, a, input[type="submit"]'));
              const candidate = buttons.find(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const href = (node.getAttribute('href') || '').toLowerCase();
                const looksLikeSend = text.includes('adventure') || text.includes('send hero') || text.includes('start') || href.includes('adventure');
                const isGold = text.includes('gold') || cls.includes('gold') || text.includes('instant');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return looksLikeSend && !isGold && !disabled;
              });
              if (!candidate) return false;
              candidate.click();
              return true;
            }
            """);
    }

    private async Task<bool?> ReadIsCapitalAsync(string villageName, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading capital state.", cancellationToken);
        var previousUrl = _page.Url;
        try
        {
            await GotoAsync("/spieler.php", cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading player profile.", cancellationToken);
            var result = await _page.EvaluateAsync<string>(
                """
                (vName) => {
                  const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
                  const capitalSpan = document.querySelector('span.mainVillage');
                  if (!capitalSpan) return 'unknown';
                  // Walk up from the span and check text content against the village name
                  let el = capitalSpan.parentElement;
                  for (let depth = 0; depth < 8 && el && el !== document.body; depth++, el = el.parentElement) {
                    const text = clean(el.textContent || '');
                    if (text.toLowerCase().includes(vName.toLowerCase())) return 'true';
                  }
                  return 'false';
                }
                """,
                villageName);

            return result?.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousUrl))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }
    }

    private async Task RefreshCapitalStateForActiveVillageAsync(CancellationToken cancellationToken)
    {
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(activeVillage))
        {
            return;
        }

        var isCapital = await ReadIsCapitalAsync(activeVillage, cancellationToken);
        SaveCachedCapitalState(activeVillage, isCapital);
    }

    private async Task RefreshCapitalStatesFromPlayerProfileAsync(CancellationToken cancellationToken)
    {
        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            await RefreshCapitalStateForActiveVillageAsync(cancellationToken);
            return;
        }

        var villageNames = villages
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => v.Name!.Trim())
            .ToArray();

        if (villageNames.Length == 0)
        {
            await RefreshCapitalStateForActiveVillageAsync(cancellationToken);
            return;
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading capital states.", cancellationToken);
        var previousUrl = _page.Url;
        try
        {
            await GotoAsync("/spieler.php", cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading player profile villages.", cancellationToken);
            await EnsureLoggedInAsync();

            // Extract capital + coordinates from spieler.php table rows.
            var profileVillages = await _page.EvaluateAsync<PlayerProfileVillageCapitalJs[]>(
                """
                (villageNames) => {
                  const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
                  const results = [];

                  // Try the table-based layout: each row has td.name (village name), td.coords (coordinates link), span.mainVillage
                  const rows = Array.from(document.querySelectorAll('table tr'));
                  for (const row of rows) {
                    const coordAnchor = row.querySelector('td.coords a[href*="karte.php"]');
                    if (!coordAnchor) continue;

                    const href = coordAnchor.getAttribute('href') || '';
                    const xMatch = href.match(/[?&]x=(-?\d+)/);
                    const yMatch = href.match(/[?&]y=(-?\d+)/);
                    const x = xMatch ? parseInt(xMatch[1], 10) : null;
                    const y = yMatch ? parseInt(yMatch[1], 10) : null;

                    const rowText = clean(row.textContent || '').toLowerCase();
                    let matchedName = null;
                    for (const name of villageNames) {
                      if (rowText.includes(name.toLowerCase())) { matchedName = name; break; }
                    }
                    if (!matchedName) continue;

                    const isCapital = !!row.querySelector('span.mainVillage, td.isCapital, .capital');
                    results.push({ name: matchedName, isCapital, x, y });
                  }

                  // Fallback: if no rows matched, at least find capital via span.mainVillage DOM walk
                  if (results.length === 0) {
                    const capitalSpan = document.querySelector('span.mainVillage');
                    if (capitalSpan) {
                      let el = capitalSpan.parentElement;
                      for (let d = 0; d < 8 && el && el !== document.body; d++, el = el.parentElement) {
                        const text = clean(el.textContent || '').toLowerCase();
                        for (const name of villageNames) {
                          if (text.includes(name.toLowerCase())) {
                            results.push({ name, isCapital: true, x: null, y: null });
                            break;
                          }
                        }
                        if (results.length > 0) break;
                      }
                    }
                  }

                  return results;
                }
                """,
                villageNames);

            if (profileVillages == null || profileVillages.Length == 0)
            {
                Notify("Capital detection: no village data found on spieler.php — skipping cache update.");
                return;
            }

            var capitalName = profileVillages.FirstOrDefault(v => v.IsCapital)?.Name ?? string.Empty;
            Notify($"Capital detection: identified capital village as '{(string.IsNullOrWhiteSpace(capitalName) ? "(none)" : capitalName)}'. Found {profileVillages.Length} villages with coords.");

            foreach (var pv in profileVillages)
            {
                SaveCachedVillageState(pv.Name ?? string.Empty, pv.IsCapital, pv.X, pv.Y);
            }

            // Also mark any known villages NOT in the profile list as non-capital (if not already set)
            foreach (var name in villageNames.Where(n => !profileVillages.Any(pv => string.Equals(pv.Name, n, StringComparison.OrdinalIgnoreCase))))
            {
                SaveCachedCapitalState(name, false);
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousUrl))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }
    }

    private async Task<string?> TryReadActiveVillageNameSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadActiveVillageNameAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private bool? TryGetCachedCapitalState(string villageName)
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return null;
        }

        EnsureCapitalCacheLoaded();
        var key = BuildCapitalCacheKey(villageName);
        lock (_capitalCacheSync)
        {
            return _capitalCacheByKey.TryGetValue(key, out var entry)
                ? entry.IsCapital
                : null;
        }
    }

    private void SaveCachedCapitalState(string villageName, bool? isCapital)
        => SaveCachedVillageState(villageName, isCapital, null, null);

    private void SaveCachedVillageState(string villageName, bool? isCapital, int? coordX, int? coordY)
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return;
        }

        lock (_capitalCacheSync)
        {
            EnsureCapitalCacheLoadedUnderLock();
            var key = BuildCapitalCacheKey(villageName);

            // Preserve existing coords if none provided; preserve existing isCapital if none provided
            _capitalCacheByKey.TryGetValue(key, out var existing);
            var resolvedIsCapital = isCapital ?? existing?.IsCapital;
            if (resolvedIsCapital is null && coordX is null && coordY is null)
            {
                return;
            }

            _capitalCacheByKey[key] = new CapitalCacheEntry
            {
                AccountName = _account.Name,
                ServerUrl = _config.BaseUrl.TrimEnd('/'),
                VillageName = villageName,
                IsCapital = resolvedIsCapital,
                CoordX = coordX ?? existing?.CoordX,
                CoordY = coordY ?? existing?.CoordY,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            PersistCapitalCacheUnderLock();
        }
    }

    private (int? X, int? Y) TryGetCachedVillageCoords(string villageName)
    {
        if (string.IsNullOrWhiteSpace(villageName))
            return (null, null);
        EnsureCapitalCacheLoaded();
        var key = BuildCapitalCacheKey(villageName);
        lock (_capitalCacheSync)
        {
            return _capitalCacheByKey.TryGetValue(key, out var entry)
                ? (entry.CoordX, entry.CoordY)
                : (null, null);
        }
    }

    private void EnsureCapitalCacheLoaded()
    {
        lock (_capitalCacheSync)
        {
            EnsureCapitalCacheLoadedUnderLock();
        }
    }

    private void EnsureCapitalCacheLoadedUnderLock()
    {
        if (_capitalCacheLoaded)
        {
            return;
        }

        _capitalCacheLoaded = true;
        _capitalCacheByKey.Clear();
        if (!File.Exists(_capitalCachePath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(_capitalCachePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<CapitalCacheDocument>(raw, CapitalCacheJsonOptions);
            if (document?.Entries is null)
            {
                return;
            }

            foreach (var entry in document.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.VillageName))
                {
                    continue;
                }

                var key = BuildCapitalCacheKey(entry.AccountName, entry.ServerUrl, entry.VillageName);
                _capitalCacheByKey[key] = entry;
            }
        }
        catch (Exception ex)
        {
            Notify($"Could not load capital cache: {ex.Message}");
        }
    }

    private void PersistCapitalCacheUnderLock()
    {
        try
        {
            var directory = Path.GetDirectoryName(_capitalCachePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Capital cache path is invalid.");
            }

            Directory.CreateDirectory(directory);
            var document = new CapitalCacheDocument
            {
                Entries = _capitalCacheByKey.Values
                    .OrderBy(item => item.AccountName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ServerUrl, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.VillageName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            File.WriteAllText(_capitalCachePath, JsonSerializer.Serialize(document, CapitalCacheJsonOptions));
        }
        catch (Exception ex)
        {
            Notify($"Could not save capital cache: {ex.Message}");
        }
    }

    private string BuildCapitalCacheKey(string villageName)
    {
        return BuildCapitalCacheKey(_account.Name, _config.BaseUrl.TrimEnd('/'), villageName);
    }

    private static string BuildCapitalCacheKey(string accountName, string serverUrl, string villageName)
    {
        return string.Join(
            "|",
            NormalizeCacheKeyPart(accountName),
            NormalizeCacheKeyPart(serverUrl),
            NormalizeCacheKeyPart(villageName));
    }

    private static string NormalizeCacheKeyPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }

    private async Task<IReadOnlyList<ResourceField>> ReadResourceFieldsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource fields.", cancellationToken);
        string rawFieldsJson = string.Empty;
        await RetryAsync("read resource fields snapshot", async () =>
        {
            rawFieldsJson = await _page.EvaluateAsync<string>(
                """
            () => {
              const fieldTypes = {
                1: 'wood',
                2: 'clay',
                3: 'iron',
                4: 'crop'
              };
              const fieldNames = {
                wood: 'Woodcutter',
                clay: 'Clay pit',
                iron: 'Iron mine',
                crop: 'Cropland',
                unknown: 'Unknown field'
              };

              const parseSlotIdFromText = (value) => {
                if (!value) return null;
                const idMatch = String(value).match(/[?&]id=(\d+)/i);
                if (idMatch) return Number(idMatch[1]);
                const aidMatch = String(value).match(/(?:^|[^a-z])aid[_:=\s-]?(\d{2})/i);
                if (aidMatch) return Number(aidMatch[1]);
                const slotMatch = String(value).match(/(?:^|[^a-z])slot[_:=\s-]?(\d{2})/i);
                if (slotMatch) return Number(slotMatch[1]);
                return null;
              };

              const parseSlotId = (element, href) => {
                const fromHref = parseSlotIdFromText(href);
                if (fromHref !== null) return fromHref;

                if (!element || typeof element.getAttribute !== 'function') return null;
                const attrs = [
                  element.getAttribute('data-aid'),
                  element.getAttribute('aid'),
                  element.getAttribute('data-id'),
                  element.getAttribute('data-slot'),
                  element.getAttribute('data-targetid'),
                  element.getAttribute('data-target-id'),
                  element.getAttribute('href'),
                  element.getAttribute('data-href'),
                  element.getAttribute('onclick'),
                  element.id || '',
                  element.className || ''
                ];

                for (const attr of attrs) {
                  const fromAttr = parseSlotIdFromText(attr);
                  if (fromAttr !== null) return fromAttr;
                }

                return null;
              };

              const directText = (element) => {
                const parts = [
                  element.getAttribute('title') || '',
                  element.getAttribute('alt') || '',
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-name') || '',
                  element.getAttribute('data-level') || '',
                  element.getAttribute('data-gid') || '',
                  element.getAttribute('data-aid') || '',
                  element.id || '',
                  element.className || '',
                  element.textContent || ''
                ];
                return parts.join(' ').replace(/\s+/g, ' ').trim();
              };

              const localText = (element) => {
                const parts = [directText(element)];
                for (const child of element.querySelectorAll('img, span, div, area')) {
                  parts.push(directText(child));
                }
                return parts.join(' ').replace(/\s+/g, ' ').trim();
              };

              const resourceLevelOverlays = Array.from(document.querySelectorAll('#village_map .level'))
                .filter((element) => /(?:^|\s)gid\d+(?:\s|$)/i.test(element.className || ''))
                .slice(0, 18);

              const overlayText = (slotId) => {
                const overlay = resourceLevelOverlays[slotId - 1];
                return overlay ? directText(overlay) : '';
              };

              const parseLevel = (element, slotId) => {
                const text = `${localText(element)} ${overlayText(slotId)}`;
                const match = text.match(/(?:^|\s|_|-)level[_-]?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:^|\s|_|-)lvl(?:e|_)?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:level|niveau|lvl|niv\.?|stufe)[^0-9]*(\d{1,2})/i);
                if (match) return Number(match[1]);
                return null;
              };

              const parseType = (element, slotId) => {
                const text = `${localText(element)} ${overlayText(slotId)}`.toLowerCase();
                const gidMatch = text.match(/(?:^|\s|_|-)gid[_-]?(\d+)(?:\s|$|_|-)/);
                if (gidMatch && fieldTypes[Number(gidMatch[1])]) return fieldTypes[Number(gidMatch[1])];

                // Travian often puts gid class on a parent container (e.g. <div class="field gid4">).
                const gidEl = element.closest('[class*="gid"]');
                if (gidEl) {
                  const ancestorGidMatch = (gidEl.className || '').toLowerCase().match(/(?:^|\s)gid[_-]?(\d+)(?:\s|$)/);
                  if (ancestorGidMatch && fieldTypes[Number(ancestorGidMatch[1])]) {
                    return fieldTypes[Number(ancestorGidMatch[1])];
                  }
                }

                if (text.includes('wood') || text.includes('lumber') || text.includes('trä')) return 'wood';
                if (text.includes('clay') || text.includes('lera')) return 'clay';
                if (text.includes('iron') || text.includes('järn')) return 'iron';
                if (text.includes('crop') || text.includes('wheat') || text.includes('gröda')) return 'crop';
                return 'unknown';
              };

              const parseName = (fieldType, element) => {
                const text = localText(element);
                const isUsefulName = (value) => {
                  if (!value || /^\d+$/.test(value) || value.length > 40) return false;
                  if (/^(gid|aid|level|lvl)/i.test(value)) return false;
                  if (/(good|resourceField|labelLayer|colorLayer|contractLink|underConstruction)/i.test(value)) return false;
                  if (/^(a|g)\d+$/i.test(value)) return false;
                  return true;
                };
                const titleLike = text
                  .replace(/(?:^|\s|_|-)gid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\s|_|-)aid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/level\s*\d+/gi, '')
                  .replace(/level\d+/gi, '')
                  .replace(/lvl\s*\d+/gi, '')
                  .replace(/lvl(?:e|_)?\d+/gi, '')
                  .replace(/niveau\s*\d+/gi, '')
                  .replace(/stufe\s*\d+/gi, '')
                  .replace(/\s+/g, ' ')
                  .trim();
                if (isUsefulName(titleLike)) return titleLike;
                return fieldNames[fieldType] || fieldNames.unknown;
              };

              const selectors = [
                '#resourceFieldContainer area[href*="build.php?id="]',
                '#rx area[href*="build.php?id="]',
                'area[href*="build.php?id="]',
                '#resourceFieldContainer a[href*="build.php?id="]',
                '#rx a[href*="build.php?id="]',
                '.resourceField a[href*="build.php?id="]',
                'a[href*="build.php?id="]'
              ];

              const seen = new Set();
              const fields = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href');
                  const slotId = parseSlotId(element, href);
                  if (slotId === null || slotId > 18) continue;
                  const key = String(slotId);
                  if (seen.has(key)) continue;
                  seen.add(key);
                  const fieldType = parseType(element, slotId);
                  fields.push({
                    slotId,
                    fieldType,
                    name: parseName(fieldType, element),
                    level: parseLevel(element, slotId),
                    href
                  });
                }
              }

              return JSON.stringify(fields);
            }
            """);
        });

        var rawFields = string.IsNullOrWhiteSpace(rawFieldsJson)
            ? new List<ResourceFieldJs>()
            : JsonSerializer.Deserialize<List<ResourceFieldJs>>(rawFieldsJson) ?? new List<ResourceFieldJs>();

        rawFields ??= [];
        var fieldTypeNames = new Dictionary<string, string>
        {
            ["wood"] = "Woodcutter",
            ["clay"] = "Clay pit",
            ["iron"] = "Iron mine",
            ["crop"] = "Cropland",
        };

        var fields = rawFields.Select(item =>
        {
            var fieldType = string.IsNullOrWhiteSpace(item.FieldType) ? "unknown" : item.FieldType!;
            var name = !string.IsNullOrWhiteSpace(item.Name)
                ? item.Name!
                : fieldTypeNames.GetValueOrDefault(fieldType, "Unknown field");
            return new ResourceField(item.SlotId, fieldType, name, item.Level, ResolveUrl(item.Href));
        }).ToList();

        var seenSlots = fields.Where(f => f.SlotId is not null).Select(f => f.SlotId!.Value).ToHashSet();
        for (var slotId = 1; slotId <= 18; slotId++)
        {
            if (seenSlots.Contains(slotId))
            {
                continue;
            }

            fields.Add(new ResourceField(
                SlotId: slotId,
                FieldType: "unknown",
                Name: "Unknown field",
                Level: null,
                Url: ResolveUrl($"build.php?id={slotId}")));
        }

        return fields.OrderBy(f => f.SlotId ?? 999).ToList();
    }

    private async Task<IReadOnlyList<Building>> ReadBuildingsAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath("/dorf2.php"))
        {
            await GotoAsync("/dorf2.php", cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the building overview.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading buildings.", cancellationToken);

        string rawBuildingsJson = string.Empty;
        await RetryAsync("read building slots snapshot", async () =>
        {
            rawBuildingsJson = await _page.EvaluateAsync<string>(
                """
            () => {
              const buildingNames = {
                10: 'Warehouse',
                11: 'Granary',
                15: 'Main Building',
                16: 'Rally Point',
                17: 'Marketplace',
                18: 'Embassy',
                19: 'Barracks',
                20: 'Stable',
                21: 'Workshop',
                22: 'Academy',
                23: 'Cranny',
                24: 'Town Hall',
                25: 'Residence',
                26: 'Palace',
                27: 'Treasury',
                28: 'Trade Office',
                29: 'Great Barracks',
                30: 'Great Stable',
                31: 'City Wall',
                32: 'Earth Wall',
                33: 'Palisade',
                34: 'Stonemason',
                35: 'Brewery',
                36: 'Trapper',
                37: 'Hero Mansion',
                38: 'Great Warehouse',
                39: 'Great Granary',
                40: 'Wonder of the World',
                41: 'Horse Drinking Trough',
                42: 'Stone Wall',
                43: 'Makeshift Wall',
                44: 'Command Center'
              };

              const parseSlotIdFromText = (value) => {
                if (!value) return null;
                const idMatch = String(value).match(/[?&]id=(\d+)/i);
                if (idMatch) return Number(idMatch[1]);
                const aidMatch = String(value).match(/(?:^|[^a-z])aid[_:=\s-]?(\d{2})/i);
                if (aidMatch) return Number(aidMatch[1]);
                const slotMatch = String(value).match(/(?:^|[^a-z])slot[_:=\s-]?(\d{2})/i);
                if (slotMatch) return Number(slotMatch[1]);
                return null;
              };

              const parseSlotId = (element, href) => {
                const fromHref = parseSlotIdFromText(href);
                if (fromHref !== null) return fromHref;
                if (!element || typeof element.getAttribute !== 'function') return null;

                const attrs = [
                  element.getAttribute('data-aid'),
                  element.getAttribute('aid'),
                  element.getAttribute('data-id'),
                  element.getAttribute('data-slot'),
                  element.getAttribute('data-targetid'),
                  element.getAttribute('data-target-id'),
                  element.getAttribute('href'),
                  element.getAttribute('data-href'),
                  element.getAttribute('onclick'),
                  element.id || '',
                  element.className || ''
                ];

                for (const attr of attrs) {
                  const fromAttr = parseSlotIdFromText(attr);
                  if (fromAttr !== null) return fromAttr;
                }

                return null;
              };

              const directText = (element) => {
                const parts = [
                  element.getAttribute('title') || '',
                  element.getAttribute('alt') || '',
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-name') || '',
                  element.getAttribute('data-level') || '',
                  element.id || '',
                  element.className || '',
                  element.textContent || ''
                ];
                return parts.join(' ').replace(/\s+/g, ' ').trim();
              };

              const collectText = (element) => {
                const parts = [];
                let current = element;
                for (let depth = 0; current && depth < 3; depth += 1) {
                  parts.push(current.getAttribute('title') || '');
                  parts.push(current.getAttribute('alt') || '');
                  parts.push(current.getAttribute('aria-label') || '');
                  parts.push(current.getAttribute('data-name') || '');
                  parts.push(current.getAttribute('data-level') || '');
                  parts.push(current.getAttribute('data-gid') || '');
                  parts.push(current.getAttribute('data-aid') || '');
                  parts.push(current.id || '');
                  parts.push(current.className || '');
                  parts.push(current.textContent || '');
                  for (const child of current.querySelectorAll('img, span, div, area')) {
                    parts.push(child.getAttribute('title') || '');
                    parts.push(child.getAttribute('alt') || '');
                    parts.push(child.getAttribute('aria-label') || '');
                    parts.push(child.getAttribute('data-name') || '');
                    parts.push(child.getAttribute('data-level') || '');
                    parts.push(child.getAttribute('data-gid') || '');
                    parts.push(child.getAttribute('data-aid') || '');
                    parts.push(child.id || '');
                    parts.push(child.className || '');
                    parts.push(child.textContent || '');
                  }
                  current = current.parentElement;
                }
                return parts.join(' ').replace(/\s+/g, ' ').trim();
              };

              const parseGid = (text) => {
                const match = text.match(/(?:^|\s|_|-)gid[_:=\s-]?(\d+)(?:\s|$|_|-)/i);
                return match ? Number(match[1]) : null;
              };

              const parseLevel = (text) => {
                const match = text.match(/(?:^|\s|_|-)level[_-]?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:^|\s|_|-)lvl(?:e|_)?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:^|\s|_|-)l(?:evel)?[_:=\s-]?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:level|niveau|lvl|niv\.?|stufe)[^0-9]*(\d{1,2})/i);
                if (match) return Number(match[1]);
                const numericOnly = text.match(/(?:^|\D)(\d{1,2})(?:\D|$)/);
                return numericOnly ? Number(numericOnly[1]) : null;
              };

              const parseName = (text, direct, gid, slotId) => {
                const source = direct || text;
                const isUsefulName = (value) => {
                  if (!value || /^\d+$/.test(value) || value.length > 48) return false;
                  if (/^(gid|aid|level|lvl)/i.test(value)) return false;
                  if (/(buildingSlot|labelLayer|colorLayer|contractLink|underConstruction)/i.test(value)) return false;
                  if (/^(a|g)\d+$/i.test(value)) return false;
                  return true;
                };
                const cleaned = text
                  .replace(/(?:^|\s|_|-)gid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\s|_|-)aid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/level\s*\d+/gi, '')
                  .replace(/level\d+/gi, '')
                  .replace(/lvl\s*\d+/gi, '')
                  .replace(/lvl(?:e|_)?\d+/gi, '')
                  .replace(/niveau\s*\d+/gi, '')
                  .replace(/stufe\s*\d+/gi, '')
                  .replace(/\s+/g, ' ')
                  .trim();
                const directCleaned = source
                  .replace(/(?:^|\s|_|-)gid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\s|_|-)aid[_-]?\d+(?:\s|$|_|-)/gi, ' ')
                  .replace(/level\s*\d+/gi, '')
                  .replace(/level\d+/gi, '')
                  .replace(/lvl\s*\d+/gi, '')
                  .replace(/lvl(?:e|_)?\d+/gi, '')
                  .replace(/niveau\s*\d+/gi, '')
                  .replace(/stufe\s*\d+/gi, '')
                  .replace(/\s+/g, ' ')
                  .trim();

                if (isUsefulName(directCleaned)) return directCleaned;
                if (isUsefulName(cleaned)) return cleaned;
                if (gid && buildingNames[gid]) return buildingNames[gid];
                return `Slot ${slotId}`;
              };

              const selectors = [
                '#village_map area[href*="build.php?id="]',
                '#villageContent area[href*="build.php?id="]',
                'area[href*="build.php?id="]',
                '#village_map a[href*="build.php?id="]',
                '#villageContent a[href*="build.php?id="]',
                '#village_map [data-href*="build.php?id="]',
                '#villageContent [data-href*="build.php?id="]',
                '#village_map [onclick*="build.php?id="]',
                '#villageContent [onclick*="build.php?id="]',
                '#village_map [data-aid]',
                '#villageContent [data-aid]',
                '#village_map .level.maxLevel',
                '#villageContent .level.maxLevel',
                '.level.maxLevel',
                '.buildingSlot a[href*="build.php?id="]',
                '.buildingSlot',
                'a[href*="build.php?id="]'
              ];

              const seenSlots = new Set();
              const buildings = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href')
                    || element.getAttribute('data-href')
                    || element.getAttribute('onclick')
                    || '';
                  const slotId = parseSlotId(element, href);
                  if (slotId === null || slotId < 19 || slotId > 40) continue;
                  if (seenSlots.has(slotId)) continue;
                  seenSlots.add(slotId);

                  const direct = directText(element);
                  const text = collectText(element);
                  const gid = parseGid(text);
                  const name = parseName(text, direct, gid, slotId);
                  buildings.push({
                    slotId,
                    name,
                    level: parseLevel(text),
                    gid,
                    href
                  });
                }
              }

              return JSON.stringify(buildings);
            }
            """);
        });

        var rawBuildings = string.IsNullOrWhiteSpace(rawBuildingsJson)
            ? new List<BuildingJs>()
            : JsonSerializer.Deserialize<List<BuildingJs>>(rawBuildingsJson) ?? new List<BuildingJs>();

        rawBuildings ??= [];
        return rawBuildings.Select(item =>
                new Building(item.SlotId, item.Name ?? "Unknown", item.Level, ResolveUrl(item.Href), item.Gid))
            .ToList();
    }

    private async Task<IReadOnlyList<BuildQueueItem>> ReadBuildQueueAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the build queue.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '.buildingList li',
                '#building_contract li',
                '.underConstruction',
                '.buildDuration',
                'table.buildingList tr'
              ];

              const items = [];
              const seen = new Set();
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const text = (element.textContent || '').replace(/\s+/g, ' ').trim();
                  if (!text || seen.has(text)) continue;
                  seen.add(text);
                  const timeElement = element.querySelector('.timer, .countdown, .value, [counting="down"], [id^="timer"]');
                  const timeLeft = timeElement ? (timeElement.textContent || '').trim() : null;
                  items.push({ text, timeLeft });
                }
                if (items.length) return JSON.stringify(items);
              }
              return JSON.stringify(items);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<BuildQueueJs>()
            : JsonSerializer.Deserialize<List<BuildQueueJs>>(rawJson) ?? new List<BuildQueueJs>();

        raw ??= [];
        return raw
            .Where(i => !string.IsNullOrWhiteSpace(i.Text))
            .Select(i => new BuildQueueItem(i.Text!, i.TimeLeft))
            .ToList();
    }

    private static int? ResolveShortestQueueDurationSeconds(IReadOnlyList<BuildQueueItem> items)
    {
        var candidates = items
            .Select(item => ParseDurationToSeconds(item.TimeLeft))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Min();
    }

    private static int? ParseDurationToSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var hms = Regex.Match(value, @"(?:(?<h>\d{1,3})\s*:)?(?<m>\d{1,2})\s*:\s*(?<s>\d{1,2})");
        if (hms.Success)
        {
            var h = hms.Groups["h"].Success ? int.Parse(hms.Groups["h"].Value) : 0;
            var m = int.Parse(hms.Groups["m"].Value);
            var s = int.Parse(hms.Groups["s"].Value);
            return Math.Max(0, h * 3600 + m * 60 + s);
        }

        var minutes = Regex.Match(value, @"(?<m>\d{1,4})\s*m(?:in|inute)?s?", RegexOptions.IgnoreCase);
        var seconds = Regex.Match(value, @"(?<s>\d{1,6})\s*s(?:ec|econd)?s?", RegexOptions.IgnoreCase);
        if (minutes.Success || seconds.Success)
        {
            var m = minutes.Success ? int.Parse(minutes.Groups["m"].Value) : 0;
            var s = seconds.Success ? int.Parse(seconds.Groups["s"].Value) : 0;
            return Math.Max(0, m * 60 + s);
        }

        return null;
    }

    private static string FormatDuration(int seconds)
    {
        var clamped = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(clamped);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private static int ComputeUpgradeWaitSeconds(int? detectedSeconds)
        => Math.Max(1, Math.Min((detectedSeconds ?? 0) + 1, 12 * 60 * 60));

    private async Task<InboxUnreadCountsJs> ReadUnreadInboxCountsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading inbox counters.", cancellationToken);

        var result = await _page.EvaluateAsync<InboxUnreadCountsJs>(
            """
            () => {
              const unreadHints = ['unread', 'new', 'notification', 'alert', 'active'];
              const countFromText = (value) => {
                const text = (value || '').replace(/\s+/g, ' ').trim();
                if (!text) {
                  return 0;
                }

                const match = text.match(/\b(\d{1,3})\b/);
                return match ? Number(match[1]) : 0;
              };

              const scoreUnread = (element) => {
                if (!element) {
                  return 0;
                }

                const attrs = [
                  element.className || '',
                  element.id || '',
                  element.getAttribute('title') || '',
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-count') || '',
                  element.textContent || ''
                ].join(' ').toLowerCase();

                const explicitCount = countFromText(attrs);
                if (explicitCount > 0) {
                  return explicitCount;
                }

                for (const hint of unreadHints) {
                  if (attrs.includes(hint)) {
                    return 1;
                  }
                }

                return 0;
              };

              const findUnreadFor = (selectors) => {
                let best = 0;
                for (const selector of selectors) {
                  const nodes = document.querySelectorAll(selector);
                  for (const node of nodes) {
                    best = Math.max(best, scoreUnread(node));
                    for (const child of node.querySelectorAll('*')) {
                      best = Math.max(best, scoreUnread(child));
                    }
                  }

                  if (best > 0) {
                    return best;
                  }
                }

                return best;
              };

              const messageSelectors = [
                'a[href*="message" i]',
                'a[href*="messages" i]',
                '#n6',
                '#navigation a[title*="message" i]',
                '[class*="message" i]'
              ];

              const reportSelectors = [
                'a[href*="report" i]',
                'a[href*="berichte" i]',
                '#n5',
                '#navigation a[title*="report" i]',
                '[class*="report" i]'
              ];

              return {
                unreadMessages: findUnreadFor(messageSelectors),
                unreadReports: findUnreadFor(reportSelectors)
              };
            }
            """);

        return result ?? new InboxUnreadCountsJs();
    }

    private async Task<bool> TryMarkInboxItemsAsReadAsync(
        IReadOnlyList<string> markSelectors,
        IReadOnlyList<string> unreadSelectorHints,
        string label,
        CancellationToken cancellationToken)
    {
        var before = await ReadUnreadInboxCountsAsync(cancellationToken);
        var beforeCount = label.Equals("reports", StringComparison.OrdinalIgnoreCase)
            ? before.UnreadReports
            : before.UnreadMessages;

        var clicked = false;
        if (label.Equals("messages", StringComparison.OrdinalIgnoreCase))
        {
            clicked = await TryMarkUnreadMessagesViaCheckboxesAsync();
        }
        else if (label.Equals("reports", StringComparison.OrdinalIgnoreCase))
        {
            clicked = await TryMarkAllReportsAsReadAsync();
        }

        foreach (var selector in markSelectors)
        {
            if (clicked)
            {
                break;
            }

            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                clicked = true;
                break;
            }
            catch
            {
                // Try next selector.
            }
        }

        if (!clicked)
        {
            clicked = await _page.EvaluateAsync<bool>(
                """
                (selectors) => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  for (const selector of selectors) {
                    for (const node of document.querySelectorAll(selector)) {
                      const text = clean(`${node.textContent || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                      if (text.includes('mark as read') || text.includes('mark all') || text.includes('als gelesen') || text.includes('read')) {
                        node.click();
                        return true;
                      }
                    }
                  }
                  return false;
                }
                """,
                markSelectors);
        }

        if (clicked)
        {
            await PauseForManualStepIfVisibleAsync($"Manual verification appeared while marking {label} as read.", cancellationToken);
            await Task.Delay(300, cancellationToken);
        }

        // Reload current inbox page to ensure counters refresh.
        await GotoAsync(_page.Url, cancellationToken);

        var after = await ReadUnreadInboxCountsAsync(cancellationToken);
        var afterCount = label.Equals("reports", StringComparison.OrdinalIgnoreCase)
            ? after.UnreadReports
            : after.UnreadMessages;

        if (afterCount < beforeCount)
        {
            return true;
        }

        if (beforeCount == 0)
        {
            return false;
        }

        // Fallback: inspect tab hints for unread classes/counters after click.
        var hintUnreads = await _page.EvaluateAsync<int>(
            """
            (selectors) => {
              const countFrom = (node) => {
                if (!node) return 0;
                const blob = `${node.className || ''} ${node.id || ''} ${node.textContent || ''} ${node.getAttribute('title') || ''}`.toLowerCase();
                const match = blob.match(/\b(\d{1,3})\b/);
                if (match) return Number(match[1]);
                return /unread|new|alert|active/.test(blob) ? 1 : 0;
              };

              let best = 0;
              for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                  best = Math.max(best, countFrom(node));
                  for (const child of node.querySelectorAll('*')) {
                    best = Math.max(best, countFrom(child));
                  }
                }
              }

              return best;
            }
            """,
            unreadSelectorHints);

        return hintUnreads == 0;
    }

    private async Task<bool> TryMarkUnreadMessagesViaCheckboxesAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const unreadMarkers = Array.from(document.querySelectorAll("img.messageStatusUnread, img[alt*='unread' i], img[src*='x.gif']"));
                  const selected = new Set();

                  for (const marker of unreadMarkers) {
                    const row = marker.closest('tr, li, .message, .inboxRow, .row, .listEntry, .entry');
                    const scope = row || marker.parentElement || document;
                    const checkbox = scope.querySelector("input.check[type='checkbox'], input[type='checkbox'][name^='n'], input[type='checkbox']");
                    if (!checkbox || checkbox.disabled || selected.has(checkbox)) {
                      continue;
                    }

                    checkbox.checked = true;
                    checkbox.dispatchEvent(new Event('input', { bubbles: true }));
                    checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                    selected.add(checkbox);
                  }

                  if (selected.size === 0) {
                    return false;
                  }

                  const isMarkAsRead = (value) => {
                    const text = clean(value);
                    return text.includes('mark as read') || text.includes('als gelesen');
                  };

                  const contentNode = Array.from(document.querySelectorAll('div.button-content'))
                    .find((node) => isMarkAsRead(node.textContent || ''));
                  if (contentNode) {
                    const clickable = contentNode.closest('button, a, div.button, li, span');
                    (clickable || contentNode).click();
                    return true;
                  }

                  const fallbackNode = Array.from(document.querySelectorAll("button, a, input[type='submit'], div.button, span"))
                    .find((node) => isMarkAsRead(`${node.textContent || ''} ${node.getAttribute('value') || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`));
                  if (!fallbackNode) {
                    return false;
                  }

                  fallbackNode.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsExecutionContextDestroyed(ex))
        {
            return true;
        }
    }

    private async Task<bool> TryMarkAllReportsAsReadAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const isMarkAllAsRead = (value) => {
                    const text = clean(value);
                    return text.includes('mark all as read') || text.includes('all as read') || text.includes('alle als gelesen');
                  };

                  const contentNode = Array.from(document.querySelectorAll('div.button-content'))
                    .find((node) => isMarkAllAsRead(node.textContent || ''));
                  if (contentNode) {
                    const clickable = contentNode.closest('button, a, div.button, li, span');
                    (clickable || contentNode).click();
                    return true;
                  }

                  const fallbackNode = Array.from(document.querySelectorAll("button, a, input[type='submit'], div.button, span"))
                    .find((node) => isMarkAllAsRead(`${node.textContent || ''} ${node.getAttribute('value') || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`));
                  if (!fallbackNode) {
                    return false;
                  }

                  fallbackNode.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsExecutionContextDestroyed(ex))
        {
            return true;
        }
    }

    private static bool IsExecutionContextDestroyed(PlaywrightException ex)
        => ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase);

    private async Task<UpgradeAttemptResult> AnalyzeUpgradeActionabilityAsync(int slotId, CancellationToken cancellationToken, bool performClick)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await GotoAsync($"/build.php?id={slotId}", cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the upgrade page.", cancellationToken);
                await EnsureLoggedInAsync();
                await EnsureExpectedBuildSlotPageAsync(slotId, "analyze upgrade");
                await ApplyActionDelayAsync(cancellationToken);

                var rawJson = await _page.EvaluateAsync<string>(
                    """
                    ({ profile }) => {
                      const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                      const textOf = (element) => clean(`${element.textContent || ''} ${element.getAttribute('value') || ''} ${element.getAttribute('title') || ''} ${element.getAttribute('aria-label') || ''}`);
                      const pageText = clean(document.body ? document.body.innerText : '').toLowerCase();
                      const normalizedProfile = clean(profile || '').toLowerCase();

                      const detectMaxLevel = () => {
                        const maxMatch = pageText.match(/max(?:imum)?[^0-9]{0,12}level[^0-9]{0,8}(\d{1,3})/i)
                          || pageText.match(/level[^0-9]{0,8}(\d{1,3})[^0-9]{0,8}max/i)
                          || pageText.match(/(?:level|lvl)[^0-9]{0,6}\d{1,3}\s*\/\s*(\d{1,3})/i);
                        return maxMatch ? Number(maxMatch[1]) : null;
                      };

                      const noneHints = Array.from(document.querySelectorAll('span.none, div.none, .none'))
                        .map((node) => clean(node.textContent || '').toLowerCase())
                        .filter((text) => text.length > 0);
                      const workersBusyHint = noneHints.find((text) => /all\s*workers\s*are\s*busy/.test(text)) || null;
                      const resourcesAvailableHint = noneHints.find((text) => /resources\s*will\s*be\s*available/.test(text)) || null;

                      const blockedByMax = /max(?:imum)?\s*level|max\s*reached|maxlevel|already\s*max/i.test(pageText);
                      const blockedByQueue = !!workersBusyHint
                        || /building\s*queue|construction\s*queue|under\s*construction|queue\s*full|busy|occupied|cannot\s*start/i.test(pageText);
                      const blockedByResources = !!resourcesAvailableHint
                        || /not\s*enough|insufficient|resources|lumber|clay|iron|crop|wood|missing\s*resources|requires\s*more/i.test(pageText);
                      const parseDurationSeconds = (raw) => {
                        const text = clean(raw || '');
                        if (!text) {
                          return null;
                        }

                        const full = text.match(/(\d{1,3})\s*:\s*(\d{1,2})\s*:\s*(\d{1,2})/);
                        if (full) {
                          return Number(full[1]) * 3600 + Number(full[2]) * 60 + Number(full[3]);
                        }

                        const short = text.match(/(^|[^\d])(\d{1,3})\s*:\s*(\d{1,2})([^\d]|$)/);
                        if (short) {
                          return Number(short[2]) * 60 + Number(short[3]);
                        }

                        const sec = text.match(/(\d{1,6})\s*s(?:ec|econd)?s?\b/i);
                        if (sec) {
                          return Number(sec[1]);
                        }

                        const min = text.match(/(\d{1,4})\s*m(?:in|inute)?s?\b/i);
                        if (min) {
                          return Number(min[1]) * 60;
                        }

                        return null;
                      };

                      const detectQueueWaitSeconds = () => {
                        const timerSelectors = [
                          '.buildingList .timer',
                          '.buildingList .countdown',
                          '.buildingList .value',
                          '#building_contract .timer',
                          '#building_contract .countdown',
                          '#building_contract .value',
                          '.underConstruction .timer',
                          '.underConstruction .countdown',
                          '.underConstruction .value',
                          '[id^="timer"]',
                          '[counting="down"]',
                          '.timer',
                          '.countdown',
                          '.value'
                        ];

                        for (const selector of timerSelectors) {
                          const nodes = document.querySelectorAll(selector);
                          for (const node of nodes) {
                            const seconds = parseDurationSeconds(node.textContent || '');
                            if (seconds && seconds > 0) {
                              return seconds;
                            }
                          }
                        }
                        return null;
                      };

                      const detectResourceWaitSeconds = () => {
                        const sources = [];
                        if (resourcesAvailableHint) {
                          sources.push(resourcesAvailableHint);
                        }
                        for (const node of document.querySelectorAll('span.none, div.none, .none, .contract, .errorMessage, .error')) {
                          const text = clean(node.textContent || '');
                          if (!text) {
                            continue;
                          }

                          if (/resources\s*will\s*be\s*available/i.test(text) || /not\s*enough|insufficient|missing\s*resources/i.test(text)) {
                            sources.push(text);
                          }
                        }

                        for (const source of sources) {
                          const seconds = parseDurationSeconds(source);
                          if (seconds && seconds > 0) {
                            return seconds;
                          }
                        }

                        return null;
                      };

                      const score = (candidate) => {
                        const green = candidate.classes.includes('green');
                        const upgradeText = candidate.text.includes('upgrade') || candidate.text.includes('build');
                        const signalClass = candidate.classes.includes('upgrade') || candidate.classes.includes('build') || candidate.classes.includes('contract');
                        const container = candidate.inUpgradeContainer;
                        if (normalizedProfile === 'strict_green') {
                          return (green ? 6 : 0) + (container ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        if (normalizedProfile === 'container_first') {
                          return (container ? 6 : 0) + (green ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        if (normalizedProfile === 'aggressive') {
                          return (signalClass ? 4 : 0) + (container ? 3 : 0) + (green ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        return (green ? 3 : 0) + (container ? 2 : 0) + (upgradeText ? 1 : 0);
                      };

                      const candidates = Array.from(document.querySelectorAll('button, input[type="submit"], input[type="button"], a'));
                      const picked = [];
                      const clickOrder = [];

                      for (let candidateIndex = 0; candidateIndex < candidates.length; candidateIndex += 1) {
                        const element = candidates[candidateIndex];
                        const text = textOf(element).toLowerCase();
                        const classes = clean(element.className || '').toLowerCase();
                        const href = (element.getAttribute('href') || '').toLowerCase();
                        const form = element.closest('form');
                        const formAction = (form ? form.getAttribute('action') : '') || '';
                        const disabled = !!(element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true');
                        const isGold = classes.includes('gold') || text.includes('gold') || text.includes('npc') || text.includes('instant');
                        const inUpgradeContainer = !!element.closest('.upgradeBuilding, .contract, .contractWrapper, .build_details, .buildingWrapper, #contract, form[action*="build.php"]');
                        const hasUpgradeSignals =
                          classes.includes('green')
                          || classes.includes('upgrade')
                          || classes.includes('build')
                          || classes.includes('contract')
                          || href.includes('build.php')
                          || formAction.includes('build.php')
                          || inUpgradeContainer;

                        if (!hasUpgradeSignals || isGold) {
                          continue;
                        }

                        picked.push({
                          text: text.slice(0, 120),
                          classes: classes.slice(0, 120),
                          disabled,
                          inUpgradeContainer
                        });

                        if (!disabled) {
                          clickOrder.push({ candidateIndex, text, classes, inUpgradeContainer });
                        }
                      }

                      clickOrder.sort((a, b) => score(b) - score(a));

                      if (clickOrder.length > 0) {
                        return JSON.stringify({
                          outcome: 'CanUpgrade',
                          reason: `Detected candidate '${clickOrder[0].text.slice(0, 80)}'`,
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds: detectQueueWaitSeconds(),
                          candidateIndex: clickOrder[0].candidateIndex,
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (blockedByMax) {
                        return JSON.stringify({
                          outcome: 'BlockedByMaxLevel',
                          reason: 'Page indicates max level reached.',
                          detectedMaxLevel: detectMaxLevel(),
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (blockedByQueue) {
                        const queueWaitSeconds = detectQueueWaitSeconds();
                        return JSON.stringify({
                          outcome: 'BlockedByQueue',
                          reason: workersBusyHint
                            ? `Page indicates workers are busy: '${workersBusyHint.slice(0, 120)}'.`
                            : 'Page indicates building queue/slot is busy.',
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds,
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (blockedByResources) {
                        const resourceWaitSeconds = detectResourceWaitSeconds();
                        return JSON.stringify({
                          outcome: 'BlockedByResources',
                          reason: resourcesAvailableHint
                            ? `Page indicates resources are not ready yet: '${resourcesAvailableHint.slice(0, 120)}'.`
                            : 'Page indicates not enough resources.',
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds: resourceWaitSeconds,
                          summary: picked.slice(0, 8)
                        });
                      }

                      return JSON.stringify({
                        outcome: 'BlockedUnknown',
                        reason: 'No actionable upgrade control found.',
                        detectedMaxLevel: detectMaxLevel(),
                        summary: picked.slice(0, 8)
                      });
                    }
                    """,
                    new
                    {
                        profile = string.IsNullOrWhiteSpace(_config.UpgradeSelectorProfile) ? "auto" : _config.UpgradeSelectorProfile
                    });

                var parsed = string.IsNullOrWhiteSpace(rawJson)
                    ? null
                    : JsonSerializer.Deserialize<UpgradeActionabilityJs>(rawJson);

                var outcome = ParseUpgradeOutcome(parsed?.Outcome);
                var reason = string.IsNullOrWhiteSpace(parsed?.Reason)
                    ? "Unknown actionability result."
                    : parsed!.Reason!;
                if ((outcome == UpgradeAttemptOutcome.BlockedByQueue || outcome == UpgradeAttemptOutcome.BlockedByResources)
                    && parsed?.QueueWaitSeconds is int waitSeconds
                    && waitSeconds > 0)
                {
                    reason = $"{reason} queue_wait_seconds={waitSeconds}";
                }
                var summary = parsed?.Summary is { Count: > 0 }
                    ? string.Join(" | ", parsed.Summary.Take(3).Select(item => $"{item.Text} [{item.Classes}] disabled={item.Disabled}"))
                    : string.Empty;

                if (performClick && outcome == UpgradeAttemptOutcome.CanUpgrade)
                {
                    await ClickDetectedUpgradeCandidateAsync(slotId, parsed?.CandidateIndex, cancellationToken);
                    reason = $"Clicked detected upgrade candidate for slot {slotId} (index {parsed?.CandidateIndex?.ToString() ?? "?"}).";
                }

                if (outcome == UpgradeAttemptOutcome.BlockedUnknown)
                {
                    if (summary.Length > 0)
                    {
                        Notify($"Upgrade actionability debug for slot {slotId}: {summary}");
                    }

                    await CaptureFailureArtifactsAsync($"upgrade-slot-{slotId}-blocked-unknown", cancellationToken);
                }

                await RetryAsync("wait for page load", async () =>
                {
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                });
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after upgrade actionability analysis.", cancellationToken);
                await ApplyActionDelayAsync(cancellationToken);

                return new UpgradeAttemptResult(
                    Outcome: outcome,
                    Reason: reason,
                    DetectedMaxLevel: parsed?.DetectedMaxLevel,
                    QueueWaitSeconds: parsed?.QueueWaitSeconds,
                    CandidateIndex: parsed?.CandidateIndex,
                    DebugSummary: summary);
            }
            catch (ManualVerificationRequiredException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3 && IsTransientExecutionContextException(ex))
            {
                Notify($"Upgrade analysis for slot {slotId} hit transient execution-context error on attempt {attempt}/3. Retrying...");
                await Task.Delay(250 * attempt, cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                await CaptureFailureArtifactsAsync($"upgrade-slot-{slotId}-exception", cancellationToken);
                throw new InvalidOperationException($"Upgrade analysis failed for slot {slotId}: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException($"Upgrade analysis failed for slot {slotId}: exhausted retries.");
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

    private async Task NavigateToResourceFieldsAfterUpgradeClickAsync(CancellationToken cancellationToken)
    {
        await GotoAsync("/dorf1.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while returning to resource fields after upgrade click.", cancellationToken);
        await EnsureLoggedInAsync();
    }

    private static int MaxLevelForBuilding(Building building)
    {
        if (building.Gid is int gid)
        {
            return BuildingCatalogService.MaxLevelFor(gid);
        }

        return 40;
    }

    private async Task<int> ResolveBuildingMaxLevelAsync(Building building, int slotId, CancellationToken cancellationToken)
    {
        var configured = MaxLevelForBuilding(building);
        var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
        if (actionability.DetectedMaxLevel is int detected && detected > 0)
        {
            if (detected != configured)
            {
                Notify($"Building max level override for slot {slotId} ({building.Name}): configured={configured}, detected={detected}");
            }

            return detected;
        }

        return configured;
    }

    private static int ResolveResourceMaxLevelFallback(bool? isCapital)
    {
        return isCapital == true ? 40 : 10;
    }

    private static UpgradeAttemptOutcome ParseUpgradeOutcome(string? value)
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

    private async Task<UpgradeProgressResult> WaitForResourceLevelAdvanceAsync(
        int slotId,
        int previousLevel,
        string queueFingerprintBefore,
        int? expectedWaitSeconds,
        CancellationToken cancellationToken)
    {
        var rawSeconds = expectedWaitSeconds ?? 0;
        var waitSeconds = ComputeUpgradeWaitSeconds(expectedWaitSeconds);
        Notify($"Resource slot {slotId} upgrade duration: {FormatDuration(rawSeconds)}. Waiting {waitSeconds}s. queue_wait_seconds={waitSeconds}");
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);

        var latestSnapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
        var current = latestSnapshot.ResourceFields.FirstOrDefault(field => field.SlotId == slotId)?.Level;
        if (current is int currentLevel && currentLevel > previousLevel)
        {
            Notify($"Resource slot {slotId} level increased from {previousLevel} to {currentLevel}.");
            return new UpgradeProgressResult(true, false, "level advanced");
        }

        var queueFingerprintAfter = BuildQueueFingerprint(latestSnapshot.BuildQueue);
        if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
        {
            return new UpgradeProgressResult(false, true, "queue changed");
        }

        if (latestSnapshot.BuildQueue.Count > 0)
        {
            return new UpgradeProgressResult(false, true, "queue has entries");
        }

        return new UpgradeProgressResult(false, false, "no queue or level change");
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

    private async Task<ResourceProgressSnapshot> ReadResourceProgressSnapshotAsync(CancellationToken cancellationToken)
    {
        await GotoAsync("/dorf1.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource progress.", cancellationToken);
        await EnsureLoggedInAsync();
        var fields = await ReadResourceFieldsAsync(cancellationToken);
        var queue = await ReadBuildQueueAsync(cancellationToken);
        return new ResourceProgressSnapshot(fields, queue);
    }

    private async Task<UpgradeProgressResult> WaitForBuildingLevelAdvanceAsync(
        int slotId,
        int previousLevel,
        string queueFingerprintBefore,
        CancellationToken cancellationToken)
    {
        VillageStatus? latestStatus = null;
        for (var i = 0; i < 4; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);
            latestStatus = await ReadVillageStatusAsync(cancellationToken);
            var current = latestStatus.Buildings.FirstOrDefault(building => building.SlotId == slotId)?.Level;
            if (current is int currentLevel && currentLevel > previousLevel)
            {
                Notify($"Building slot {slotId} level increased from {previousLevel} to {currentLevel}.");
                return new UpgradeProgressResult(true, false, "level advanced");
            }
        }

        var queueFingerprintAfter = latestStatus is null
            ? string.Empty
            : BuildQueueFingerprint(latestStatus.BuildQueue);
        if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
        {
            return new UpgradeProgressResult(false, true, "queue changed");
        }

        if (latestStatus is not null && latestStatus.BuildQueue.Count > 0)
        {
            return new UpgradeProgressResult(false, true, "queue has entries");
        }

        return new UpgradeProgressResult(false, false, "no queue or level change");
    }

    private static void EnsureBuildingRequirementsMet(VillageStatus status, int? gid, string name)
    {
        if (gid is null)
        {
            return;
        }

        var missing = MissingBuildingRequirements(status, gid.Value);
        if (missing.Count == 0)
        {
            return;
        }

        var requirements = string.Join(", ", missing.Select(item => $"{item.name} level {item.level}"));
        throw new InvalidOperationException($"{name} cannot be upgraded yet. Missing requirements: {requirements}.");
    }

    private static void EnsureBuildingCanBeConstructed(VillageStatus status, int gid, string name)
    {
        var existing = status.Buildings
            .Where(building => building.Gid == gid || SameBuildingName(building.Name, name))
            .ToList();
        var duplicateAllowed = gid is 23 or 38 or 39;
        var wallGid = gid is 31 or 32 or 33 or 42 or 43;
        if (gid is 10 or 11)
        {
            if (existing.Count > 0)
            {
                var highest = existing
                    .Where(building => building.Level is not null)
                    .Select(building => building.Level!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                if (highest < 40)
                {
                    throw new InvalidOperationException($"{name} can only be duplicated after an existing one reaches level 40.");
                }
            }
        }
        else if (existing.Count > 0 && !duplicateAllowed && !wallGid)
        {
            throw new InvalidOperationException($"{name} already exists in this village.");
        }

        var missing = MissingBuildingRequirements(status, gid);
        if (missing.Count == 0)
        {
            return;
        }

        var requirements = string.Join(", ", missing.Select(item => $"{item.name} level {item.level}"));
        throw new InvalidOperationException($"{name} cannot be built yet. Missing requirements: {requirements}.");
    }

    private async Task EnsureServerAllowsConstructionAsync(int slotId, int gid, string name, CancellationToken cancellationToken)
    {
        var choices = await ReadServerBuildChoicesOnCurrentPageAsync(cancellationToken);
        if (choices.Count == 0)
        {
            return;
        }

        var match = choices.FirstOrDefault(choice => choice.Gid == gid);
        if (match is null)
        {
            throw new InvalidOperationException($"{name} is not listed by the server for slot {slotId}.");
        }

        if (!match.Available)
        {
            var reason = string.IsNullOrWhiteSpace(match.Reason) ? string.Empty : $" Server reason: {match.Reason}";
            throw new InvalidOperationException($"{name} cannot be built in slot {slotId} right now.{reason}");
        }
    }

    private async Task<IReadOnlyList<ServerBuildChoice>> ReadServerBuildChoicesOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading build choices.", cancellationToken);

        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const parseGid = (element) => {
                const text = [
                  element.getAttribute('href') || '',
                  element.getAttribute('onclick') || '',
                  element.getAttribute('class') || '',
                  element.getAttribute('data-gid') || '',
                  element.textContent || ''
                ].join(' ');
                const match = text.match(/(?:gid=|gid%3D|gid\s*)(\d+)/i) || text.match(/(?:^|\s)gid(\d+)(?:\s|$)/i);
                return match ? Number(match[1]) : null;
              };

              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const rows = Array.from(document.querySelectorAll(
                '.contract, .buildingWrapper, .build_details, .buildingList li, table tr, div'
              ));
              const seen = new Set();
              const choices = [];

              for (const row of rows) {
                const gid = parseGid(row);
                if (!gid || seen.has(gid)) continue;
                seen.add(gid);

                const button = row.querySelector('button, input[type="submit"], a[href*="gid"]') || row;
                const classes = clean(`${row.className || ''} ${button.className || ''}`).toLowerCase();
                const text = clean(row.textContent || '');
                const lowerText = text.toLowerCase();
                const disabled = button.disabled || classes.includes('disabled') || lowerText.includes('not enough')
                  || lowerText.includes('requirements') || lowerText.includes('missing') || lowerText.includes('cannot');
                const isGold = classes.includes('gold') || lowerText.includes('npc') || lowerText.includes('instant');
                const available = !disabled && !isGold && (
                  classes.includes('green') || lowerText.includes('build') || lowerText.includes('construct')
                );
                const heading = row.querySelector('h2, h3, .title, .name, img[alt]');
                const name = clean(heading ? (heading.getAttribute('alt') || heading.textContent) : text.split('\n')[0]);
                choices.push({
                  gid,
                  name: name || `gid ${gid}`,
                  available,
                  reason: available ? 'Server says available' : text
                });
              }

              return JSON.stringify(choices);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<ServerBuildChoiceJs>()
            : JsonSerializer.Deserialize<List<ServerBuildChoiceJs>>(rawJson) ?? new List<ServerBuildChoiceJs>();

        return raw
            .Where(item => item.Gid is not null)
            .Select(item => new ServerBuildChoice(
                Gid: item.Gid!.Value,
                Name: string.IsNullOrWhiteSpace(item.Name) ? $"gid {item.Gid}" : item.Name!,
                Available: item.Available,
                Reason: item.Reason ?? string.Empty))
            .ToList();
    }

    private static List<(string name, int level)> MissingBuildingRequirements(VillageStatus status, int gid)
    {
        var missing = new List<(string name, int level)>();
        if (!BuildingRequirements.TryGetValue(gid, out var requirements))
        {
            return missing;
        }

        foreach (var (requiredName, requiredLevel) in requirements)
        {
            var current = BuildingLevelByName(status, requiredName);
            if (current < requiredLevel)
            {
                missing.Add((requiredName, requiredLevel));
            }
        }

        return missing;
    }

    private static int BuildingLevelByName(VillageStatus status, string name)
    {
        var matches = status.Buildings
            .Where(building => SameBuildingName(building.Name, name))
            .Select(building => building.Level ?? 0)
            .ToList();

        return matches.Count > 0 ? matches.Max() : 0;
    }

    private static bool SameBuildingName(string left, string right)
    {
        return NormalizeBuildingName(left) == NormalizeBuildingName(right);
    }

    private static string NormalizeBuildingName(string name)
    {
        var cleaned = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant();
        return cleaned switch
        {
            "granary / silo" => "granary",
            "silo" => "granary",
            "city wall" => "wall",
            "earth wall" => "wall",
            "palisade" => "wall",
            "stone wall" => "wall",
            "makeshift wall" => "wall",
            _ => cleaned,
        };
    }

    private static string BuildQueueFingerprint(IReadOnlyList<BuildQueueItem> queue)
    {
        if (queue.Count == 0)
        {
            return "empty";
        }

        return string.Join(
            " || ",
            queue
                .Take(5)
                .Select(item => $"{item.Text.Trim()}|{item.TimeLeft?.Trim() ?? string.Empty}"));
    }

    private async Task EnsureExpectedBuildSlotPageAsync(int slotId, string operationLabel)
    {
        var currentUrl = _page.Url;
        var currentSlotId = ExtractSlotIdFromUrl(currentUrl);
        if (currentSlotId != slotId)
        {
            throw new InvalidOperationException(
                $"{operationLabel} expected build.php?id={slotId}, but current url is '{currentUrl}'.");
        }

        var hasBuildContext = await _page.EvaluateAsync<bool>(
            """
            () => !!document.querySelector(
              "form[action*='build.php' i], .upgradeBuilding, .contract, .buildingWrapper, a[href*='build.php?id=']"
            )
            """);
        if (!hasBuildContext)
        {
            throw new InvalidOperationException(
                $"{operationLabel} expected a build slot context, but required build controls were not found.");
        }
    }

    private static int? ExtractSlotIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=(\d+)");
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var slotId)
            ? slotId
            : null;
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

    private static string SafePathSegment(string value)
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

    private enum UpgradeAttemptOutcome
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

    private sealed record ResourceProgressSnapshot(
        IReadOnlyList<ResourceField> ResourceFields,
        IReadOnlyList<BuildQueueItem> BuildQueue);

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

    private sealed class InboxUnreadCountsJs
    {
        [JsonPropertyName("unreadMessages")]
        public int UnreadMessages { get; init; }

        [JsonPropertyName("unreadReports")]
        public int UnreadReports { get; init; }
    }

    private sealed class CapitalCacheDocument
    {
        [JsonPropertyName("entries")]
        public List<CapitalCacheEntry> Entries { get; init; } = [];
    }

    private sealed class CapitalCacheEntry
    {
        [JsonPropertyName("accountName")]
        public string AccountName { get; init; } = string.Empty;

        [JsonPropertyName("serverUrl")]
        public string ServerUrl { get; init; } = string.Empty;

        [JsonPropertyName("villageName")]
        public string VillageName { get; init; } = string.Empty;

        [JsonPropertyName("isCapital")]
        public bool? IsCapital { get; init; }

        [JsonPropertyName("coordX")]
        public int? CoordX { get; init; }

        [JsonPropertyName("coordY")]
        public int? CoordY { get; init; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }

    private sealed class PlayerProfileVillageCapitalJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("isCapital")]
        public bool IsCapital { get; init; }

        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }
    }

    private sealed class HeroStatusJs
    {
        [JsonPropertyName("exists")]
        public bool Exists { get; init; }

        [JsonPropertyName("isDead")]
        public bool IsDead { get; init; }

        [JsonPropertyName("hpPercent")]
        public int? HpPercent { get; init; }

        [JsonPropertyName("adventuresAvailable")]
        public int? AdventuresAvailable { get; init; }

        [JsonPropertyName("secondsUntilAdventureReady")]
        public int? SecondsUntilAdventureReady { get; init; }

        [JsonPropertyName("secondsUntilReturn")]
        public int? SecondsUntilReturn { get; init; }

        [JsonPropertyName("unassignedPoints")]
        public int? UnassignedPoints { get; init; }
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
}
