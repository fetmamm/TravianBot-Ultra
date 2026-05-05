using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<VillageStatus> ReadVillageResourceStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadVillageResourceStatusAsync started");
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening resource fields.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        return await ReadCurrentVillageResourceStatusAsync(cancellationToken);
    }

    public async Task NavigateToResourceFieldsAsync(CancellationToken cancellationToken = default)
    {
        Notify("NavigateToResourceFieldsAsync started");
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync();
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
        var safetyCap = ComputeResourceUpgradeSafetyCap(targetLevel);
        int? lastKnownLevel = null;
        for (var iteration = 0; iteration < safetyCap; iteration++)
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
                lastKnownLevel = currentLevel;
                var resourceName = string.IsNullOrWhiteSpace(field?.Name) ? $"slot {slotId}" : field!.Name;

                if (currentLevel >= targetLevel)
                {
                    return $"Resource slot {slotId} is level {currentLevel}. Target {targetLevel} reached after {upgrades} upgrades.";
                }

                // Pre-flight: if the build queue is already full (1 slot non-Plus, 2 slots Plus,
                // separate resource slot for Romans), defer the task in the program queue rather
                // than navigating to build.php and clicking only to be rejected. We are on dorf1
                // here from the snapshot read, so the queue DOM is available without navigation.
                var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Resource, slotId, upgrades, cancellationToken);
                if (deferMessage is not null)
                {
                    return deferMessage;
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
                await WaitForPostUpgradeClickPageLoadAsync(cancellationToken);
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
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, expectedWaitSeconds, cancellationToken);
                    return $"Resource slot {slotId}: queued upgrade toward level {effectiveTarget}. Evidence: {progress.Evidence}. queue_wait_seconds={queuedWaitSeconds}";
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

        var levelText = lastKnownLevel is int level ? level.ToString() : "unknown";
        return $"Resource slot {slotId}: hit safety cap of {safetyCap} iterations while targeting level {targetLevel}. Upgrades performed: {upgrades}. Last known level: {levelText}.";
    }

    internal static int ComputeResourceUpgradeSafetyCap(int targetLevel)
        => Math.Max(10, targetLevel + 8);

    public async Task<string> UpgradeAllResourcesToLevelAsync(int targetLevel, CancellationToken cancellationToken = default)
    {
        Notify($"[UpgradeAllResourcesToLevelAsync] targetLevel={targetLevel} started");
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
                await GotoAsync(Paths.Resources, cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource fields.", cancellationToken);
                await EnsureLoggedInAsync();
                var resourceFields = await ReadResourceFieldsAsync(cancellationToken);
                NotifyResourceLevelIncreases(knownLevelsBySlot, resourceFields);
                knownLevelsBySlot = BuildResourceLevelMap(resourceFields);
                var waited = await WaitForConstructionSlotIfBusyAsync(ConstructionKind.Resource, cancellationToken);
                if (waited > 0)
                {
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
                        var queueFingerprintBefore = BuildQueueFingerprint(await ReadBuildQueueAsync(cancellationToken));
                        var rawUpgradeSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken);
                        await ClickDetectedUpgradeCandidateAsync(slot, actionability.CandidateIndex, cancellationToken);
                        await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                        upgrades += 1;
                        var progress = await WaitForResourceLevelAdvanceAsync(
                            slot,
                            level,
                            queueFingerprintBefore,
                            rawUpgradeSeconds,
                            cancellationToken);
                        if (!progress.Advanced && !progress.QueuedOrInProgress)
                        {
                            var upgradeWaitSeconds = ComputeResourceUpgradeWaitSeconds(rawUpgradeSeconds);
                            Notify($"Resource slot {slot}: upgrade click did not confirm immediately ({progress.Evidence}). Waiting {upgradeWaitSeconds}s before retry.");
                            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, upgradeWaitSeconds)), cancellationToken);
                        }
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

                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                    {
                        var waitSeconds = ClampResourceWaitSeconds(actionability.QueueWaitSeconds);
                        Notify($"Resource slot {slot} blocked by resources. Waiting {waitSeconds}s. queue_wait_seconds={waitSeconds}");
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
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
                    if (!IsCurrentUrlForPath(Paths.Resources))
                    {
                        await GotoAsync(Paths.Resources, cancellationToken);
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

    private async Task<VillageStatus> ReadCurrentVillageResourceStatusAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading resource status.", cancellationToken);
        var villages = await ReadVillagesPreferCacheAsync(cancellationToken);
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
            // Update the in-memory list with the new capital flag instead of refetching
            // from spieler.php — that re-fetch would cause a visible page navigation.
            villages = villages
                .Select(v => string.Equals(v.Name, activeVillage, StringComparison.Ordinal)
                    ? v with { IsCapital = true }
                    : v)
                .ToList();
            UpdateCachedVillages(villages);
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

    internal static int? TryParseResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : null;
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
        }, cancellationToken: cancellationToken);

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

    private async Task NavigateToResourceFieldsAfterUpgradeClickAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while returning to resource fields after upgrade click.", cancellationToken);
        await EnsureLoggedInAsync();
    }

    internal static int ResolveResourceMaxLevelFallback(bool? isCapital)
    {
        return isCapital == true ? 40 : 10;
    }

    private async Task<UpgradeProgressResult> WaitForResourceLevelAdvanceAsync(
        int slotId,
        int previousLevel,
        string queueFingerprintBefore,
        int? expectedWaitSeconds,
        CancellationToken cancellationToken)
    {
        ResourceProgressSnapshot? latestSnapshot = null;
        for (var i = 0; i < 4; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);
            latestSnapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
            var current = latestSnapshot.ResourceFields.FirstOrDefault(field => field.SlotId == slotId)?.Level;
            if (current is int currentLevel && currentLevel > previousLevel)
            {
                Notify($"Resource slot {slotId} level increased from {previousLevel} to {currentLevel}.");
                return new UpgradeProgressResult(true, false, "level advanced");
            }
        }

        if (latestSnapshot is null)
        {
            latestSnapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
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

        var waitSeconds = ComputeResourceUpgradeWaitSeconds(expectedWaitSeconds);
        if (waitSeconds > 0)
        {
            return new UpgradeProgressResult(false, false, $"no immediate queue evidence; expected_wait_seconds={waitSeconds}");
        }

        return new UpgradeProgressResult(false, false, "no queue or level change");
    }

    private async Task<ResourceProgressSnapshot> ReadResourceProgressSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
            await EnsureLoggedInAsync();
        }
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource progress.", cancellationToken);
        var fields = await ReadResourceFieldsAsync(cancellationToken);
        var queue = await ReadBuildQueueAsync(cancellationToken);
        return new ResourceProgressSnapshot(fields, queue);
    }

    private static int ComputeResourceUpgradeWaitSeconds(int? detectedSeconds)
    {
        var seconds = Math.Max(0, detectedSeconds ?? 0);
        if (seconds == 0)
        {
            return 0;
        }

        return Math.Min(seconds + 1, 12 * 60 * 60);
    }

    private async Task<int> ReadQueuedResourceWaitSecondsAsync(
        string resourceName,
        int? fallbackSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = await ReadActiveConstructionsAsync(cancellationToken);
            var resourceTimers = active
                .Where(item => item.Kind == ConstructionKind.Resource && item.TimeLeftSeconds is int seconds && seconds > 0)
                .ToList();
            if (resourceTimers.Count == 0)
            {
                return ComputeResourceUpgradeWaitSeconds(fallbackSeconds);
            }

            var matchingTimers = resourceTimers
                .Where(item => SameBuildingName(item.Name, resourceName))
                .Select(item => item.TimeLeftSeconds!.Value)
                .ToList();
            if (matchingTimers.Count > 0)
            {
                return ComputeResourceUpgradeWaitSeconds(matchingTimers.Min());
            }

            return ComputeResourceUpgradeWaitSeconds(resourceTimers.Min(item => item.TimeLeftSeconds!.Value));
        }
        catch
        {
            return ComputeResourceUpgradeWaitSeconds(fallbackSeconds);
        }
    }

    private sealed record ResourceProgressSnapshot(
        IReadOnlyList<ResourceField> ResourceFields,
        IReadOnlyList<BuildQueueItem> BuildQueue);

}
