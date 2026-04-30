using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
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

        await GotoAsync(Paths.Buildings, cancellationToken);
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

        await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
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

        await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading build choices.", cancellationToken);
        await EnsureLoggedInAsync();
        return await ReadServerBuildChoicesOnCurrentPageAsync(cancellationToken);
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
        await GotoAsync(Paths.BuildBySlot(mainBuildingSlotId), cancellationToken);
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

    private async Task<IReadOnlyList<Building>> ReadBuildingsAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Buildings))
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the building overview.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading buildings.", cancellationToken);

        Dictionary<int, BuildingInfo> buildingsBySlot = new();
        await RetryAsync("read building slots snapshot", async () =>
        {
            buildingsBySlot = await ReadBuildingInfosAsync(cancellationToken);
        });

        return buildingsBySlot.Values
            .OrderBy(item => item.SlotId)
            .Select(item => new Building(
                item.SlotId,
                item.BuildingName,
                item.Level,
                ResolveUrl(Paths.BuildBySlot(item.SlotId)),
                ParseGidFromBuildingCode(item.BuildingCode)))
            .ToList();
    }

    private async Task<Dictionary<int, BuildingInfo>> ReadBuildingInfosAsync(CancellationToken cancellationToken)
    {
        var buildings = new Dictionary<int, BuildingInfo>();
        var slots = _page.Locator("div.buildingSlot");
        var count = await slots.CountAsync();

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slot = slots.Nth(index);
            try
            {
                var classText = await slot.GetAttributeAsync("class") ?? string.Empty;
                var classes = classText
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var slotId = TryExtractSlotId(classes);
                if (slotId is null || slotId < 19 || slotId > 40)
                {
                    continue;
                }

                var buildingCode = TryExtractBuildingCode(classes);
                var buildingName = ResolveBuildingName(buildingCode);
                var level = await TryReadBuildingLevelAsync(slot);

                buildings[slotId.Value] = new BuildingInfo
                {
                    SlotId = slotId.Value,
                    BuildingCode = buildingCode ?? string.Empty,
                    BuildingName = buildingName,
                    Level = level,
                };
            }
            catch
            {
                // Keep scanning remaining slots even if one slot is malformed or transiently missing content.
            }
        }

        return buildings;
    }

    private async Task<int> TryReadBuildingLevelAsync(ILocator slot)
    {
        try
        {
            var label = slot.Locator(".labelLayer").First;
            if (await label.CountAsync() == 0)
            {
                return 0;
            }

            var rawLevel = (await label.TextContentAsync() ?? string.Empty).Trim();
            return int.TryParse(rawLevel, out var level) ? level : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int? TryExtractSlotId(IEnumerable<string> classes)
    {
        string? fallback = null;
        foreach (var className in classes)
        {
            if (className.StartsWith("aid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(className[3..], out var aidSlotId))
            {
                return aidSlotId;
            }

            if (fallback is null
                && className.StartsWith("a", StringComparison.OrdinalIgnoreCase)
                && !className.StartsWith("aid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(className[1..], out _))
            {
                fallback = className;
            }
        }

        return fallback is not null && int.TryParse(fallback[1..], out var slotId)
            ? slotId
            : null;
    }

    private static string? TryExtractBuildingCode(IEnumerable<string> classes)
    {
        foreach (var className in classes)
        {
            if (className.StartsWith("g", StringComparison.OrdinalIgnoreCase)
                && className.Length > 1
                && int.TryParse(className[1..], out _))
            {
                return className.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string ResolveBuildingName(string? buildingCode)
    {
        if (string.IsNullOrWhiteSpace(buildingCode))
        {
            return "Empty";
        }

        return TravianBuildings.TryGetValue(buildingCode, out var buildingName)
            ? buildingName
            : buildingCode;
    }

    private static int? ParseGidFromBuildingCode(string? buildingCode)
    {
        if (string.IsNullOrWhiteSpace(buildingCode) || buildingCode.Length < 2)
        {
            return null;
        }

        return int.TryParse(buildingCode[1..], out var gid)
            ? gid
            : null;
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

    internal static int? ResolveShortestQueueDurationSeconds(IReadOnlyList<BuildQueueItem> items)
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

    internal static int? ParseDurationToSeconds(string? raw)
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

    internal static string FormatDuration(int seconds)
    {
        var clamped = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(clamped);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    internal static int ComputeUpgradeWaitSeconds(int? detectedSeconds)
        => Math.Max(1, Math.Min((detectedSeconds ?? 0) + 1, 12 * 60 * 60));


    private async Task<UpgradeAttemptResult> AnalyzeUpgradeActionabilityAsync(int slotId, CancellationToken cancellationToken, bool performClick)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
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

    internal static int MaxLevelForBuilding(Building building)
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
        foreach (var requirement in BuildingCatalogService.RequirementsFor(gid))
        {
            var current = BuildingLevelByName(status, requirement.Name);
            if (current < requirement.Level)
            {
                missing.Add((requirement.Name, requirement.Level));
            }
        }

        return missing;
    }

    internal static int BuildingLevelByName(VillageStatus status, string name)
    {
        var matches = status.Buildings
            .Where(building => SameBuildingName(building.Name, name))
            .Select(building => building.Level ?? 0)
            .ToList();

        return matches.Count > 0 ? matches.Max() : 0;
    }

    internal static bool SameBuildingName(string left, string right)
    {
        return NormalizeBuildingName(left) == NormalizeBuildingName(right);
    }

    internal static string NormalizeBuildingName(string name)
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

    internal static string BuildQueueFingerprint(IReadOnlyList<BuildQueueItem> queue)
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

    internal static int? ExtractSlotIdFromUrl(string? url)
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

    private static readonly Dictionary<string, string> TravianBuildings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g10"] = "Warehouse",
        ["g11"] = "Granary",
        ["g15"] = "Main Building",
        ["g16"] = "Rally Point",
        ["g17"] = "Marketplace",
        ["g18"] = "Embassy",
        ["g19"] = "Barracks",
        ["g20"] = "Stable",
        ["g21"] = "Workshop",
        ["g22"] = "Academy",
        ["g23"] = "Cranny",
        ["g24"] = "Town Hall",
        ["g25"] = "Residence",
        ["g26"] = "Palace",
        ["g27"] = "Treasury",
        ["g28"] = "Trade Office",
        ["g29"] = "Great Barracks",
        ["g30"] = "Great Stable",
        ["g31"] = "City Wall",
        ["g32"] = "Earth Wall",
        ["g33"] = "Palisade",
        ["g34"] = "Stonemason",
        ["g35"] = "Brewery",
        ["g36"] = "Trapper",
        ["g37"] = "Hero's Mansion",
        ["g38"] = "Great Warehouse",
        ["g39"] = "Great Granary",
        ["g40"] = "Wonder of the World",
        ["g41"] = "Horse Drinking Trough",
        ["g42"] = "Stone Wall",
        ["g43"] = "Makeshift Wall",
        ["g44"] = "Command Center",
    };

    private sealed class BuildingInfo
    {
        public int SlotId { get; set; }
        public string BuildingCode { get; set; } = string.Empty;
        public string BuildingName { get; set; } = "Empty";
        public int Level { get; set; }
    }

}
