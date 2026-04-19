using Microsoft.Playwright;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;

namespace TbotUltra.Worker.Services;

public sealed class TravianClient
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
    private readonly Action<string>? _statusCallback;
    private DateTimeOffset? _serverTimeUtc;

    public TravianClient(
        IPage page,
        BotOptions config,
        AccountOptions account,
        bool interactive = true,
        bool browserVisible = true,
        Action<string>? statusCallback = null)
    {
        _page = page;
        _config = config;
        _account = account;
        _interactive = interactive;
        _browserVisible = browserVisible;
        _statusCallback = statusCallback;
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before login.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            Notify("Already logged in.");
            return;
        }

        await GotoAsync(_config.LoginPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared on the login page.", cancellationToken);
        if (await IsLoggedInAsync())
        {
            Notify("Already logged in.");
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
    }

    public async Task<VillageStatus> ReadVillageStatusAsync(CancellationToken cancellationToken = default)
    {
        await GotoAsync(_config.VillageOverviewPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the village overview.", cancellationToken);
        await EnsureLoggedInAsync();
        return await ReadCurrentVillageStatusAsync(cancellationToken);
    }

    public async Task<AccountSnapshot> ReadAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageStatusesAsync(CancellationToken cancellationToken = default)
    {
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
        if (slotId < 1 || slotId > 18)
        {
            throw new InvalidOperationException($"Resource slot {slotId} is outside the resource field range.");
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
            var field = status.ResourceFields.FirstOrDefault(item => item.SlotId == slotId);
            var currentLevel = field?.Level;
            if (currentLevel is null)
            {
                throw new InvalidOperationException($"Could not read level for resource slot {slotId}.");
            }

            if (currentLevel >= targetLevel)
            {
                return $"Resource slot {slotId} is level {currentLevel}. Target {targetLevel} reached after {upgrades} upgrades.";
            }

            var clicked = await UpgradeSlotOnceAsync(slotId, cancellationToken);
            if (!clicked)
            {
                return $"Resource slot {slotId} stopped at level {currentLevel}. Upgrade button was not available.";
            }

            upgrades += 1;
        }
    }

    public async Task<string> UpgradeResourceToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default)
    {
        if (slotId < 1 || slotId > 18)
        {
            throw new InvalidOperationException($"Resource slot {slotId} is outside the resource field range.");
        }

        var upgrades = 0;
        int? lastLevel = null;

        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await ReadVillageStatusAsync(cancellationToken);
            var field = status.ResourceFields.FirstOrDefault(item => item.SlotId == slotId);
            var currentLevel = field?.Level;
            if (currentLevel is null)
            {
                throw new InvalidOperationException($"Could not read level for resource slot {slotId}.");
            }

            if (lastLevel is not null && currentLevel <= lastLevel && upgrades > 0)
            {
                return $"Resource slot {slotId} stopped at level {currentLevel}. Level did not increase.";
            }

            lastLevel = currentLevel;
            var clicked = await UpgradeSlotOnceAsync(slotId, cancellationToken);
            if (!clicked)
            {
                return $"Resource slot {slotId} stopped at level {currentLevel}. Upgrade button was not available.";
            }

            upgrades += 1;
        }

        return $"Resource slot {slotId} reached max attempt limit after {upgrades} upgrades.";
    }

    public async Task<string> UpgradeAllResourcesToLevelAsync(int targetLevel, CancellationToken cancellationToken = default)
    {
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Target level must be 0 or higher.");
        }

        var upgrades = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await ReadVillageStatusAsync(cancellationToken);
            var candidates = status.ResourceFields
                .Where(field => field.SlotId is not null && field.Level is not null && field.Level < targetLevel)
                .OrderBy(field => field.Level ?? 0)
                .ThenBy(field => field.SlotId ?? 999)
                .ToList();

            if (candidates.Count == 0)
            {
                return $"All readable resource fields have reached level {targetLevel}. Upgrades made: {upgrades}.";
            }

            var nextField = candidates[0];
            var clicked = await UpgradeSlotOnceAsync(nextField.SlotId ?? 0, cancellationToken);
            if (!clicked)
            {
                return $"Stopped after {upgrades} upgrades. Slot {nextField.SlotId} at level {nextField.Level} could not be upgraded now.";
            }

            upgrades += 1;
        }
    }

    public async Task<string> UpgradeBuildingToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default)
    {
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

            var maxLevel = MaxLevelForBuilding(building!);
            if (targetLevel > maxLevel)
            {
                throw new InvalidOperationException($"{building!.Name} can only be upgraded to level {maxLevel}. Requested level {targetLevel}.");
            }

            if (currentLevel >= targetLevel)
            {
                return $"Building slot {slotId} is level {currentLevel}. Target {targetLevel} reached after {upgrades} upgrades.";
            }

            EnsureBuildingRequirementsMet(status, building!.Gid, building.Name);
            var clicked = await UpgradeSlotOnceAsync(slotId, cancellationToken);
            if (!clicked)
            {
                return $"Building slot {slotId} stopped at level {currentLevel}. Upgrade button was not available.";
            }

            upgrades += 1;
        }
    }

    public async Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default)
    {
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
                var maxLevel = MaxLevelForBuilding(building);
                if (building.Level >= maxLevel)
                {
                    return $"Building slot {slotId} is already at max level {maxLevel}. Upgrades made: {upgrades}.";
                }

                EnsureBuildingRequirementsMet(status, building.Gid, building.Name);
            }

            var clicked = await UpgradeSlotOnceAsync(slotId, cancellationToken);
            if (!clicked)
            {
                return $"Building slot {slotId} stopped after {upgrades} upgrades. Upgrade button was not available.";
            }

            upgrades += 1;
        }

        return $"Building slot {slotId} reached max attempt limit after {upgrades} upgrades.";
    }

    public async Task<string> ConstructBuildingAsync(int slotId, int gid, string name, CancellationToken cancellationToken = default)
    {
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
        await ApplyActionDelayAsync(cancellationToken);
        await EnsureServerAllowsConstructionAsync(slotId, gid, buildingName, cancellationToken);

        var clicked = await RetryTruthyAsync(
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
                    const classes = (element.className || '').toString().toLowerCase();
                    const disabled = element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true';
                    const isGold = classes.includes('gold') || text.includes('npc') || text.includes('instant');
                    const gidMatches = href.includes(`gid=${gidText}`) || href.includes(`gid%3D${gidText}`) || classes.includes(`gid${gidText}`);
                    const looksBuildable = text.includes('build') || text.includes('construct') || classes.includes('green');
                    if (!disabled && !isGold && gidMatches && looksBuildable) {
                      element.click();
                      return true;
                    }
                  }
                  return false;
                }
                """,
                new { gid }));

        if (!clicked)
        {
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
    }

    private async Task<VillageStatus> ReadCurrentVillageStatusAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading village status.", cancellationToken);
        return new VillageStatus(
            ActiveVillage: await ReadActiveVillageNameAsync(cancellationToken),
            Villages: await ReadVillagesAsync(cancellationToken),
            Resources: await ReadResourcesAsync(cancellationToken),
            ResourceFields: await ReadResourceFieldsAsync(cancellationToken),
            Buildings: await ReadBuildingsAsync(cancellationToken),
            BuildQueue: await ReadBuildQueueAsync(cancellationToken),
            ServerTimeUtc: _serverTimeUtc);
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

    private async Task<bool> CaptchaOrManualStepVisibleAsync()
    {
        var selectors = new[]
        {
            "input[name*='captcha' i]",
            "input[id*='captcha' i]",
            "input[placeholder*='captcha' i]",
            "img[src*='captcha' i]",
            "iframe[src*='captcha' i]",
            "iframe[src*='recaptcha' i]",
            ".g-recaptcha",
            "[class*='captcha' i]",
            "[id*='captcha' i]",
            "text=/captcha/i",
            "text=/recaptcha/i",
            "text=/verification/i",
            "text=/verify/i",
        };

        foreach (var selector in selectors)
        {
            if (await _page.Locator(selector).CountAsync() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> WaitUntilLoggedInAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _config.ManualLoginTimeoutSeconds));
        var manualMessageShown = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            .Select(v => new Village(v[0], ResolveUrl(v.Count > 1 ? v[1] : string.Empty)))
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

    private async Task<string> ReadActiveVillageNameAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the active village.", cancellationToken);
        var value = await _page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
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

    private async Task<IReadOnlyList<ResourceField>> ReadResourceFieldsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource fields.", cancellationToken);
        var rawFieldsJson = await _page.EvaluateAsync<string>(
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
              const slotFallbackTypes = {
                1: 'wood', 2: 'clay', 3: 'iron', 4: 'crop', 5: 'wood', 6: 'clay',
                7: 'iron', 8: 'crop', 9: 'crop', 10: 'wood', 11: 'iron', 12: 'crop',
                13: 'crop', 14: 'iron', 15: 'clay', 16: 'wood', 17: 'crop', 18: 'clay'
              };

              const parseSlotId = (href) => {
                if (!href) return null;
                const match = href.match(/[?&]id=(\d+)/);
                return match ? Number(match[1]) : null;
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

                if (text.includes('wood') || text.includes('lumber') || text.includes('trä')) return 'wood';
                if (text.includes('clay') || text.includes('lera')) return 'clay';
                if (text.includes('iron') || text.includes('järn')) return 'iron';
                if (text.includes('crop') || text.includes('wheat') || text.includes('gröda')) return 'crop';
                return slotFallbackTypes[slotId] || 'unknown';
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
                  const slotId = parseSlotId(href);
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

        var rawFields = string.IsNullOrWhiteSpace(rawFieldsJson)
            ? new List<ResourceFieldJs>()
            : JsonSerializer.Deserialize<List<ResourceFieldJs>>(rawFieldsJson) ?? new List<ResourceFieldJs>();

        rawFields ??= [];
        var fallbackTypes = new Dictionary<int, string>
        {
            [1] = "wood", [2] = "clay", [3] = "iron", [4] = "crop", [5] = "wood", [6] = "clay",
            [7] = "iron", [8] = "crop", [9] = "crop", [10] = "wood", [11] = "iron", [12] = "crop",
            [13] = "crop", [14] = "iron", [15] = "clay", [16] = "wood", [17] = "crop", [18] = "clay",
        };
        var fallbackNames = new Dictionary<string, string>
        {
            ["wood"] = "Woodcutter",
            ["clay"] = "Clay pit",
            ["iron"] = "Iron mine",
            ["crop"] = "Cropland",
            ["unknown"] = "Unknown field",
        };

        var fields = rawFields.Select(item =>
        {
            var fieldType = string.IsNullOrWhiteSpace(item.FieldType) ? "unknown" : item.FieldType!;
            var name = !string.IsNullOrWhiteSpace(item.Name)
                ? item.Name!
                : fallbackNames.GetValueOrDefault(fieldType, "Unknown field");
            return new ResourceField(item.SlotId, fieldType, name, item.Level, ResolveUrl(item.Href));
        }).ToList();

        var seenSlots = fields.Where(f => f.SlotId is not null).Select(f => f.SlotId!.Value).ToHashSet();
        for (var slotId = 1; slotId <= 18; slotId++)
        {
            if (seenSlots.Contains(slotId))
            {
                continue;
            }

            var fieldType = fallbackTypes.GetValueOrDefault(slotId, "unknown");
            fields.Add(new ResourceField(
                SlotId: slotId,
                FieldType: fieldType,
                Name: fallbackNames.GetValueOrDefault(fieldType, "Unknown field"),
                Level: null,
                Url: ResolveUrl($"build.php?id={slotId}")));
        }

        return fields.OrderBy(f => f.SlotId ?? 999).ToList();
    }

    private async Task<IReadOnlyList<Building>> ReadBuildingsAsync(CancellationToken cancellationToken)
    {
        await GotoAsync("/dorf2.php", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the building overview.", cancellationToken);
        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading buildings.", cancellationToken);

        var rawBuildingsJson = await _page.EvaluateAsync<string>(
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

              const parseSlotId = (href) => {
                if (!href) return null;
                const match = href.match(/[?&]id=(\d+)/);
                return match ? Number(match[1]) : null;
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
                const match = text.match(/(?:^|\s|_|-)gid[_-]?(\d+)(?:\s|$|_|-)/i);
                return match ? Number(match[1]) : null;
              };

              const parseLevel = (text) => {
                const match = text.match(/(?:^|\s|_|-)level[_-]?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:^|\s|_|-)lvl(?:e|_)?(\d{1,2})(?:\s|$|_|-)/i)
                  || text.match(/(?:level|niveau|lvl|niv\.?|stufe)[^0-9]*(\d{1,2})/i);
                return match ? Number(match[1]) : null;
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
                '.buildingSlot a[href*="build.php?id="]',
                'a[href*="build.php?id="]'
              ];

              const seenSlots = new Set();
              const buildings = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href');
                  const slotId = parseSlotId(href);
                  if (slotId === null || slotId < 19) continue;
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
                  const timeElement = element.querySelector('.timer, .countdown, [id^="timer"]');
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

    private async Task<bool> UpgradeSlotOnceAsync(int slotId, CancellationToken cancellationToken)
    {
        await GotoAsync($"/build.php?id={slotId}", cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the upgrade page.", cancellationToken);
        await EnsureLoggedInAsync();
        await ApplyActionDelayAsync(cancellationToken);

        var clicked = await RetryTruthyAsync("click upgrade button", async () =>
            await _page.EvaluateAsync<bool>(
                """
                () => {
                  const labels = ['upgrade', 'build', 'construct'];
                  const candidates = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
                  for (const element of candidates) {
                    const text = `${element.textContent || ''} ${element.getAttribute('value') || ''} ${element.getAttribute('title') || ''}`.toLowerCase();
                    const classes = (element.className || '').toString().toLowerCase();
                    const disabled = element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true';
                    const isGold = classes.includes('gold') || text.includes('gold') || text.includes('npc') || text.includes('instant');
                    const looksLikeUpgrade = labels.some((label) => text.includes(label)) || classes.includes('green');
                    if (!disabled && !isGold && looksLikeUpgrade) {
                      element.click();
                      return true;
                    }
                  }
                  return false;
                }
                """));
        if (!clicked)
        {
            return false;
        }

        await RetryAsync("wait for page load", async () =>
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        });
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking upgrade.", cancellationToken);
        await ApplyActionDelayAsync(cancellationToken);
        return true;
    }

    private static int MaxLevelForBuilding(Building building)
    {
        if (building.Gid is int gid && BuildingMaxLevelsByGid.TryGetValue(gid, out var maxLevel))
        {
            return maxLevel;
        }

        if (building.Name.Contains("cranny", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return 20;
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
        var duplicateAllowed = gid is 10 or 11 or 23 or 38 or 39;
        var wallGid = gid is 31 or 32 or 33 or 42 or 43;
        if (existing.Count > 0 && !duplicateAllowed && !wallGid)
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

    private async Task RetryAsync(string label, Func<Task> action, int attempts = 3)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (ManualVerificationRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= attempts)
                {
                    break;
                }

                Notify($"{label} failed on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay(400 * attempt);
            }
        }

        throw new InvalidOperationException($"{label} failed after {attempts} attempts: {lastError?.Message}", lastError);
    }

    private async Task<bool> RetryTruthyAsync(string label, Func<Task<bool>> action, int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var result = await action();
            if (result)
            {
                return true;
            }

            if (attempt < attempts)
            {
                Notify($"{label} was not available on attempt {attempt}/{attempts}. Retrying...");
                await Task.Delay(400 * attempt);
            }
        }

        return false;
    }

    private void Notify(string message)
    {
        _statusCallback?.Invoke(message);
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
}
