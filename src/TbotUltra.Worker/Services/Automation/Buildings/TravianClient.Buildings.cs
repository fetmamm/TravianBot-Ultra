using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Building surface of the TravianClient facade. The interface list is declared
// on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IBuildingClient
{

    public async Task<VillageStatus> ReadBuildingsStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("[build:verbose] ReadBuildingsStatusAsync started");
        var buildings = await ReadBuildingsAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var tribe = await ReadTribeAsync(cancellationToken);

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: [],
            Tribe: tribe,
            VillageCount: 0,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc);
    }

    public async Task<string> DemolishBuildingToLevelAsync(
        string targetBuildingSlotOrName,
        int targetLevel,
        CancellationToken cancellationToken = default)
    {
        Notify($"[demolish] starting — target='{targetBuildingSlotOrName}', targetLevel={targetLevel}");
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Demolish target level must be >= 0.");
        }

        if (!int.TryParse(targetBuildingSlotOrName.Trim(), out var slotId))
        {
            throw new InvalidOperationException($"Demolish requires a numeric slot id, got '{targetBuildingSlotOrName}'.");
        }
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Demolish slot {slotId} is outside the building range.");
        }

        const int safetyCap = 30;

        // One-shot: read dorf2 to get original level + main building slot id.
        await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification on dorf2.", cancellationToken);

        var initialSlots = await ReadBuildingInfosAsync(cancellationToken);
        if (!initialSlots.TryGetValue(slotId, out var initialInfo) || initialInfo.Level <= 0)
        {
            return $"Slot {slotId}: nothing to demolish (already empty).";
        }
        if (initialInfo.Level <= targetLevel)
        {
            return $"Slot {slotId}: already at level {initialInfo.Level} (target {targetLevel}).";
        }

        var mainSlot = initialSlots
            .Where(kvp => ParseGidFromBuildingCode(kvp.Value.BuildingCode) == 15)
            .OrderByDescending(kvp => kvp.Value.Level)
            .Select(kvp => (int?)kvp.Key)
            .FirstOrDefault();
        if (mainSlot is null)
        {
            throw new InvalidOperationException("Demolition requires Main Building.");
        }

        var originalLevel = initialInfo.Level;
        var targetBuildingName = string.IsNullOrWhiteSpace(initialInfo.BuildingName)
            ? $"slot {slotId}"
            : initialInfo.BuildingName;
        var demolitions = 0;
        var currentLevel = originalLevel;

        // Stay on the Main Building page across iterations — only reload there between steps.
        var mainBuildingPath = Paths.BuildBySlot(mainSlot.Value);

        for (var iter = 0; iter < safetyCap && currentLevel > targetLevel; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Select target + click demolish. TryStartDemolitionStepAsync navigates to the
            // main building page (or reloads it if already there), so each step starts fresh.
            var started = await TryStartDemolitionStepAsync(
                mainBuildingSlotId: mainSlot.Value,
                targetSlotId: slotId,
                targetBuildingName: targetBuildingName,
                cancellationToken);
            if (!started)
            {
                // The demolish form is hidden while a demolition is already running.
                // Wait for any in-progress demolition to finish, then retry once before giving up.
                var pending = await WaitForActiveDemolitionToFinishAsync(mainBuildingPath, cancellationToken);
                if (pending)
                {
                    started = await TryStartDemolitionStepAsync(
                        mainBuildingSlotId: mainSlot.Value,
                        targetSlotId: slotId,
                        targetBuildingName: targetBuildingName,
                        cancellationToken);
                }
            }

            if (!started)
            {
                return $"Slot {slotId}: could not start demolition (main building page didn't expose a demolish action). Steps: {demolitions}.";
            }

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Continue — the wait loop below copes with partial loads.
            }

            demolitions += 1;
            currentLevel -= 1;
            Notify($"Slot {slotId}: demolish step {demolitions} queued (was level {currentLevel + 1}). Waiting for it to complete.");

            // Wait for this demolition to actually complete (read its real countdown timer)
            // before reloading and starting the next level — otherwise the form is unavailable.
            await WaitForActiveDemolitionToFinishAsync(mainBuildingPath, cancellationToken);
        }

        return $"Demolished slot {slotId} from level {originalLevel} to {currentLevel} in {demolitions} step(s).";
    }

    public async Task<string> UpgradeBuildingToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default)
    {
        using var navDiagnostics = BeginConstructionNavigationDiagnostics($"upgrade_building_to_level slot={slotId} target={targetLevel}");
        Notify($"[build] upgrade starting — slot={slotId}, target=lvl {targetLevel}");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }
        if (targetLevel < 1)
        {
            throw new InvalidOperationException("Target level must be 1 or higher.");
        }

        var upgrades = 0;
        var safetyCap = UpgradeMath.ComputeBuildingUpgradeSafetyCap(targetLevel);
        int? lastKnownLevel = null;
        var transientRetries = 0;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttemptedOffers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var iteration = 0; iteration < safetyCap; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {

            // Step 1: read this slot's level. Prefer the current build page when it is already
            // the correct non-stale slot; fall back to the dorf2 overview when the page snapshot
            // cannot prove the level/name.
            var info = await ReadBuildingInfoForUpgradeAsync(slotId, cancellationToken);
            if (info is null)
            {
                return $"Slot {slotId}: not found on dorf2. Upgrades performed: {upgrades}.";
            }
            var currentLevel = info.Level;
            lastKnownLevel = currentLevel;
            if (currentLevel >= targetLevel)
            {
                return $"Slot {slotId}: already at level {currentLevel} (target {targetLevel}). Upgrades performed: {upgrades}.";
            }
            var buildingName = string.IsNullOrWhiteSpace(info.BuildingName) ? $"slot {slotId}" : info.BuildingName;
            var highestKnownLevel = await ReadHighestKnownQueuedBuildingLevelAsync(buildingName, currentLevel, cancellationToken);
            if (highestKnownLevel >= targetLevel)
            {
                var queuedWaitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, 0, cancellationToken);
                return $"Slot {slotId}: upgrade to level {targetLevel} already queued and still in progress. Upgrades performed: {upgrades}. queue_wait_seconds={queuedWaitSeconds}";
            }
            var nextLevel = highestKnownLevel + 1;
            var buildQueueBefore = await ReadBuildQueueAsync(cancellationToken);

            // Tribe/Plus-aware slot gate: if the build queue is full, defer this task back to
            // the program queue (queue_wait_seconds) rather than blocking the worker thread.
            var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, upgrades, cancellationToken);
            if (deferMessage is not null)
            {
                return deferMessage;
            }

            // Step 3: open or reuse the slot's build page.
            await EnsureCurrentBuildPageForActionAsync(slotId, "upgrade", cancellationToken);
            // Wait for the build slot controls to render before reading/clicking. GotoAsync only waits
            // for DOMContentLoaded, which does not guarantee the upgrade button is present yet (slow
            // pages / official &reload=auto timer pages). Reuse the same readiness gate as the
            // actionability analysis: it reloads once if not ready and returns immediately when already
            // rendered, so we never read/click a half-loaded page without slowing the happy path.

            // Step 4: read the upgrade duration so we know how long to wait.
            var pageAnalysis = await ReadConstructionPageAnalysisAsync(slotId, "upgrade pre-click", cancellationToken);
            var durationSeconds = pageAnalysis.DurationSeconds;
            // Read the population increase this level grants before clicking (page changes after).
            var populationDelta = pageAnalysis.PopulationDelta;

            // Step 5: click the "Upgrade to level N" button.
            var clicked = await TryUseConstructFasterForBuildAsync(
                slotId,
                ParseGidFromBuildingCode(info.BuildingCode),
                buildingName,
                currentLevel,
                nextLevel,
                buildQueueBefore,
                durationSeconds,
                null,
                cancellationToken);
            var usedConstructFasterVideo = clicked;
            if (!clicked)
            {
                clicked = await ClickUpgradeToLevelButtonAsync(slotId, nextLevel, cancellationToken);
            }
            if (!clicked)
            {
                pageAnalysis = await ReadConstructionPageAnalysisAsync(
                    slotId,
                    "upgrade no-click",
                    cancellationToken,
                    includeUpgradeActionability: true);
                var state = pageAnalysis.State;
                if (state == BuildPageState.AtMaxLevel)
                {
                    return $"Slot {slotId}: at max level (page reports max). Upgrades performed: {upgrades}.";
                }
                if (state == BuildPageState.WorkersBusy)
                {
                    Notify($"Slot {slotId}: workers busy, waiting 2s before retry. Upgrades so far: {upgrades}.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }
                if (state == BuildPageState.EmptyConstructionSlot)
                {
                    return $"Slot {slotId} is empty — construct the building before upgrading "
                        + $"(page shows a construction menu, not an 'Upgrade to level {nextLevel}' button). "
                        + $"Upgrades performed: {upgrades}.";
                }

                var actionability = pageAnalysis.UpgradeActionability ?? new UpgradeAttemptResult(
                    UpgradeAttemptOutcome.BlockedUnknown,
                    "Construction page analysis did not return upgrade actionability.",
                    null,
                    null,
                    null,
                    null,
                    string.Empty);

                // The current build page already has the exact resource block and hero-transfer control.
                // Use it before any dorf2 queue probe so a normal top-up does not navigate away and back.
                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources
                    && pageAnalysis.LooksBlockedByResources)
                {
                    var offerLevel = actionability.DetectedTargetLevel ?? nextLevel;
                    var offerCost = await TryReadLiveResourceUpgradeCostOnCurrentPageAsync(cancellationToken);
                    var offerKey = BuildConstructionHeroTransferOfferKey(slotId, offerLevel, offerCost);
                    if (heroTransferAttemptedOffers.Add(offerKey))
                    {
                        var label = $"Building slot {slotId} ({buildingName}) upgrade to level {offerLevel}";
                        Notify($"[build] hero-transfer offer key={offerKey} label='{label}'.");
                        if (await TryHeroResourceTransferForConstructionAsync(label, cancellationToken))
                        {
                            // The transfer topped up resources and reloaded the build page. Retry construct-faster
                            // before falling back to the normal button; resource shortage can disable the video
                            // button until this point.
                            await EnsureExpectedBuildSlotPageAsync(slotId, "upgrade after hero transfer", cancellationToken);
                            await ActionPacer.FromOptions(_config, Notify).DelayAsync(
                                _config.ActionPacingPageLoadMinSeconds,
                                _config.ActionPacingPageLoadMaxSeconds,
                                cancellationToken,
                                "after hero transfer reload");
                            clicked = await TryUseConstructFasterForBuildAsync(
                                slotId,
                                ParseGidFromBuildingCode(info.BuildingCode),
                                buildingName,
                                currentLevel,
                                nextLevel,
                                buildQueueBefore,
                                durationSeconds,
                                null,
                                cancellationToken);
                            if (!clicked)
                            {
                                clicked = await ClickUpgradeToLevelButtonAsync(slotId, nextLevel, cancellationToken);
                            }
                        }
                    }
                }

                if (!clicked)
                {
                    // Defense-in-depth for ambiguous/failed transfer states: verify queue capacity before
                    // NPC trade or defer classification. This probe may navigate to dorf2, so restore the
                    // slot page afterwards for the remaining build-page reads.
                    var queueDefer = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, upgrades, cancellationToken);
                    if (queueDefer is not null)
                    {
                        return queueDefer;
                    }
                    await EnsureExpectedBuildSlotPageAsync(slotId, "resource recheck after queue gate", cancellationToken);
                }

                if (!clicked && !constructionNpcTradeAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    constructionNpcTradeAttempted = true;
                    if (await TryNpcTradeForConstructionAsync($"Building slot {slotId} ({buildingName}) upgrade to level {actionability.DetectedTargetLevel ?? nextLevel}", cancellationToken))
                    {
                        continue;
                    }
                }

                // Still not clicked (transfer/NPC didn't unblock it) → classify and defer.
                if (!clicked)
                {
                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                    {
                        var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                            $"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}",
                            UpgradeMath.ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                            cancellationToken);
                        return BuildUpgradeResourceBlockedResultMessage(snapshot);
                    }
                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByQueue)
                    {
                        var waitSeconds = UpgradeMath.ClampResourceWaitSeconds(actionability.QueueWaitSeconds);
                        // Always defer queue waits (release the browser; the queue retries the item).
                        // A zero wait means the queue should already be free — retry in place.
                        if (waitSeconds > 0)
                        {
                            return $"Slot {slotId} blocked by queue. queue_wait_seconds={waitSeconds}";
                        }
                        continue;
                    }
                    var candidateSummary = string.IsNullOrWhiteSpace(actionability.DebugSummary) ? "none" : actionability.DebugSummary;
                    return $"Slot {slotId}: could not find 'Upgrade to level {nextLevel}' button. "
                        + $"Reason: {actionability.Outcome} ({actionability.Reason}). "
                        + $"url='{_page.Url}' candidates=[{candidateSummary}]. "
                        + $"Upgrades performed: {upgrades}.";
                }
            }

            upgrades += 1;
            var constructFasterResultNote = usedConstructFasterVideo ? " 25% faster (video)." : string.Empty;
            Notify($"[build] {(usedConstructFasterVideo ? "25% faster video ok" : "click ok")} — slot={slotId} '{buildingName}' lvl {currentLevel} → {nextLevel} queued (duration~{durationSeconds}s, pop +{populationDelta?.ToString() ?? "?"})");
            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            var postClickWaitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, durationSeconds, cancellationToken);
            transientRetries = 0;
            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, currentLevel, buildingName, buildQueueBefore, ParseGidFromBuildingCode(info.BuildingCode), nextLevel, cancellationToken);
            if (!progress.Advanced && !progress.QueuedOrInProgress)
            {
                // Final dorf2 probe before deferring — instant servers complete builds before the
                // queue can hold them, so the level on dorf2 is the only reliable success signal.
                var dorf2Level = await ProbeSlotLevelOnDorf2Async(slotId, cancellationToken);
                if (dorf2Level is int confirmedLevel && confirmedLevel > currentLevel)
                {
                    upgrades += 1;
                    Notify($"Slot {slotId}: upgrade confirmed via dorf2 level read ({currentLevel} → {confirmedLevel}).");
                    if (confirmedLevel >= targetLevel)
                    {
                        return $"Slot {slotId}: reached level {confirmedLevel} (target {targetLevel}). Upgrades performed: {upgrades}.{constructFasterResultNote}";
                    }
                    continue;
                }

                var waitMs = ComputePostActionWaitMs(durationSeconds);
                var waitSeconds = Math.Max(1, (int)Math.Ceiling(waitMs / 1000d));
                Notify($"Slot {slotId}: upgrade click did not confirm immediately ({progress.Evidence}). Deferring {waitSeconds}s before retry.");
                return $"Slot {slotId}: upgrade click did not confirm immediately ({progress.Evidence}). queue_wait_seconds={waitSeconds}";
            }

            if (progress.Advanced)
            {
                Notify($"[build] level advance confirmed — slot={slotId} '{buildingName}' now lvl {nextLevel} (target {targetLevel}, upgrades this run: {upgrades})");
                if (nextLevel >= targetLevel)
                {
                    return $"Slot {slotId}: reached level {nextLevel} (target {targetLevel}). Upgrades performed: {upgrades}.{constructFasterResultNote}";
                }

                continue;
            }

            if (nextLevel < targetLevel)
            {
                continue;
            }

            return $"Slot {slotId}: upgrade to level {nextLevel} queued and still in progress. Target level {targetLevel}. Upgrades performed: {upgrades}.{constructFasterResultNote} queue_wait_seconds={postClickWaitSeconds}";

            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex) && transientRetries < 6)
            {
                transientRetries += 1;
                Notify($"UpgradeBuildingToLevelAsync slot={slotId} hit transient navigation error ({transientRetries}/6): {ex.Message}. Retrying...");
                await Task.Delay(400 * transientRetries, cancellationToken);
            }
        }

        var levelText = lastKnownLevel is int level ? level.ToString() : "unknown";
        return $"Slot {slotId}: hit safety cap of {safetyCap} iterations while targeting level {targetLevel}. Upgrades performed: {upgrades}. Last known level: {levelText}.";
    }

    private async Task<UpgradeResourceWaitSnapshot> ReadUpgradeResourceWaitSnapshotAsync(
        string blockedLabel,
        int fallbackWaitSeconds,
        CancellationToken cancellationToken)
    {
        // A hero transfer skipped by the per-resource use limit already computed the exact wait from the
        // cached/known production (time until the village accumulates enough that the hero share fits the
        // limit). Return it directly without any page reads — on a build page the production/resource
        // widgets aren't present, so reading them here would trigger slow failing retries. The construction
        // defer (queue_wait_seconds) drives the countdown timer, and the build retries when it elapses.
        var serverStorageBlockKind = await ReadStorageCapacityBlockKindOnCurrentPageAsync(cancellationToken);
        if (_heroTransferOverLimitWaitSeconds is int heroUseLimitWait && serverStorageBlockKind is null)
        {
            _heroTransferOverLimitWaitSeconds = null;
            var heroLimitSnapshot = new UpgradeResourceWaitSnapshot(
                blockedLabel,
                new Dictionary<string, UpgradeResourceWaitValue>(StringComparer.OrdinalIgnoreCase),
                Math.Max(1, heroUseLimitWait),
                "hero_use_limit",
                null,
                null,
                null);
            Notify(FormatUpgradeResourceWaitLog(heroLimitSnapshot));
            return heroLimitSnapshot;
        }

        _heroTransferOverLimitWaitSeconds = null;

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading upgrade resource requirements.", cancellationToken);
        var required = await _page.EvaluateAsync<Dictionary<string, long?>>(
            """
            () => {
              const keys = ['wood', 'clay', 'iron', 'crop'];
              const iconClasses = { wood: 'r1', clay: 'r2', iron: 'r3', crop: 'r4' };
              const readCost = (key) => {
                const iconClass = iconClasses[key];
                const containers = [
                  ...document.querySelectorAll('.upgradeBuilding, .contract, .contractWrapper, .build_details, #contract, form[action*="build.php"], .inlineIconList')
                ];

                const parseNumber = (value) => {
                  const digits = (value || '').replace(/[^\d-]/g, '');
                  if (!digits) return null;
                  const parsed = Number(digits);
                  return Number.isFinite(parsed) ? parsed : null;
                };

                const extractFromRoot = (root) => {
                  if (!root) return null;
                  const iconNode = root.querySelector(`i.${iconClass}, .${iconClass}, i.${iconClass}Big, .${iconClass}Big`);
                  if (!iconNode) return null;

                  const valueNode =
                    iconNode.closest('.inlineIcon')?.querySelector('.value')
                    || iconNode.parentElement?.querySelector('.value')
                    || iconNode.nextElementSibling;
                  const parsed = parseNumber(valueNode?.textContent || '');
                  if (parsed !== null) return parsed;

                  const row = iconNode.closest('tr, li, .inlineIcon, .contract, .row, .value, div');
                  return parseNumber(row?.textContent || '');
                };

                for (const container of containers) {
                  const parsed = extractFromRoot(container);
                  if (parsed !== null) return parsed;
                }

                const globals = Array.from(document.querySelectorAll(`i.${iconClass}, .${iconClass}, i.${iconClass}Big, .${iconClass}Big`));
                for (const node of globals) {
                  const parsed = extractFromRoot(node.parentElement || node.closest('tr, li, .inlineIcon, .contract, .row, div'));
                  if (parsed !== null) return parsed;
                }

                return null;
              };

              const result = {};
              for (const key of keys) {
                result[key] = readCost(key);
              }

              return result;
            }
            """);
        (long? Warehouse, long? Granary) capacities;
        try
        {
            capacities = await ReadStorageCapacitiesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"Storage capacity read for blocked construction skipped: {ex.Message}");
            capacities = (null, null);
        }

        var hasProductionTable = await CurrentPageHasProductionTableAsync(cancellationToken);
        if (!hasProductionTable)
        {
            var currentResourcesFromStockBar = await ReadResourcesAsync(cancellationToken);
            var cachedProductionByHour = await ReadCachedProductionByHourForActiveVillageAsync(cancellationToken);
            var snapshot = BuildUpgradeResourceWaitSnapshotFromValues(
                blockedLabel,
                required,
                currentResourcesFromStockBar,
                cachedProductionByHour,
                fallbackWaitSeconds,
                HasAnyProduction(cachedProductionByHour) ? "cached_production" : "page_timer",
                capacities.Warehouse,
                capacities.Granary,
                serverStorageBlockKind);
            Notify(FormatUpgradeResourceWaitLog(snapshot));
            return snapshot;
        }

        var currentResources = await ReadResourcesAsync(cancellationToken);
        var productionByHour = await ReadResourceProductionPerHourAsync(cancellationToken);
        var liveSnapshot = BuildUpgradeResourceWaitSnapshotFromValues(
            blockedLabel,
            required,
            currentResources,
            productionByHour,
            fallbackWaitSeconds,
            "estimated_from_page",
            capacities.Warehouse,
            capacities.Granary,
            serverStorageBlockKind);
        Notify(FormatUpgradeResourceWaitLog(liveSnapshot));
        return liveSnapshot;
    }

    private static UpgradeResourceWaitSnapshot BuildUpgradeResourceWaitSnapshotFromValues(
        string blockedLabel,
        IReadOnlyDictionary<string, long?> required,
        IReadOnlyDictionary<string, string> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        int fallbackWaitSeconds,
        string waitReasonWhenEstimated,
        long? warehouseCapacity,
        long? granaryCapacity,
        string? serverStorageBlockKind = null)
    {
        var values = new Dictionary<string, UpgradeResourceWaitValue>(StringComparer.OrdinalIgnoreCase);
        var longestFiniteSeconds = 0;
        var hasUnknownWait = false;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            required.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentRaw);
            productionByHour.TryGetValue(key, out var productionValue);
            var currentValue = TravianParsing.TryParseResourceValue(currentRaw);
            var missingValue = requiredValue.HasValue && currentValue.HasValue
                ? Math.Max(0, requiredValue.Value - currentValue.Value)
                : (long?)null;

            int? waitSeconds = null;
            string waitReason;
            if (missingValue is null || missingValue <= 0)
            {
                waitSeconds = 0;
                waitReason = "enough";
            }
            else if (productionValue is > 0)
            {
                var computedSeconds = (int)Math.Ceiling((missingValue.Value / productionValue.Value) * 3600d);
                waitSeconds = Math.Max(1, computedSeconds);
                longestFiniteSeconds = Math.Max(longestFiniteSeconds, waitSeconds.Value);
                waitReason = waitReasonWhenEstimated == "cached_production" ? "from_cached_production" : "from_production";
            }
            else
            {
                hasUnknownWait = true;
                waitReason = productionValue is null ? "production_unknown" : "production_non_positive";
            }

            values[key] = new UpgradeResourceWaitValue(
                Required: requiredValue,
                Current: currentValue,
                Missing: missingValue,
                ProductionPerHour: productionValue,
                WaitSeconds: waitSeconds,
                WaitReason: waitReason);
        }

        var resolvedWaitSeconds = fallbackWaitSeconds > 0
            ? fallbackWaitSeconds
            : longestFiniteSeconds > 0
                ? longestFiniteSeconds
                : 60;
        if (hasUnknownWait && fallbackWaitSeconds <= 0)
        {
            resolvedWaitSeconds = Math.Max(30, Math.Min(resolvedWaitSeconds, 60));
        }

        var resolvedWaitReason = fallbackWaitSeconds > 0
            ? "page_timer"
            : waitReasonWhenEstimated;
        var storageCapacityKind = ResolveStorageCapacityBlockKind(
            required,
            warehouseCapacity,
            granaryCapacity,
            serverStorageBlockKind);
        if (storageCapacityKind is not null)
        {
            resolvedWaitReason = "storage_capacity";
        }

        return new UpgradeResourceWaitSnapshot(
            blockedLabel,
            values,
            resolvedWaitSeconds,
            storageCapacityKind is not null
                ? resolvedWaitReason
                : hasUnknownWait && fallbackWaitSeconds <= 0
                    ? "recheck_needed"
                    : resolvedWaitReason,
            warehouseCapacity,
            granaryCapacity,
            storageCapacityKind);
    }

    private static string? ResolveStorageCapacityBlockKind(
        IReadOnlyDictionary<string, long?> required,
        long? warehouseCapacity,
        long? granaryCapacity,
        string? serverStorageBlockKind)
    {
        var normalizedServerKind = NormalizeStorageCapacityKind(serverStorageBlockKind);
        if (normalizedServerKind is not null
            && (warehouseCapacity is not > 0 || granaryCapacity is not > 0))
        {
            return normalizedServerKind;
        }

        required.TryGetValue("wood", out var wood);
        required.TryGetValue("clay", out var clay);
        required.TryGetValue("iron", out var iron);
        required.TryGetValue("crop", out var crop);
        var requiredWarehouse = Math.Max(wood ?? 0, Math.Max(clay ?? 0, iron ?? 0));
        if (warehouseCapacity is > 0 && requiredWarehouse > warehouseCapacity.Value)
        {
            return "warehouse";
        }

        if (granaryCapacity is > 0 && crop is long requiredCrop && requiredCrop > granaryCapacity.Value)
        {
            return "granary";
        }

        return normalizedServerKind;
    }

    private static string? NormalizeStorageCapacityKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("warehouse", StringComparison.OrdinalIgnoreCase))
        {
            return "warehouse";
        }

        if (normalized.Contains("granary", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("silo", StringComparison.OrdinalIgnoreCase))
        {
            return "granary";
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<string, double?>> ReadCachedProductionByHourForActiveVillageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
            return TryGetCachedVillageResourceSnapshot(activeVillage)?.ProductionByHour
                ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"Cached production fallback unavailable: {ex.Message}");
            return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<bool> CurrentPageHasProductionTableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while checking production table.", cancellationToken);
            return await _page.EvaluateAsync<bool>("() => !!document.querySelector('#production')");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private static string FormatUpgradeResourceWaitLog(UpgradeResourceWaitSnapshot snapshot)
    {
        static string FormatValue(long? value) => value?.ToString() ?? "?";
        static string FormatProduction(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        static string FormatWait(int? value) => value is null ? "?" : value.Value.ToString();

        var parts = new List<string>();
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            if (!snapshot.Values.TryGetValue(key, out var value))
            {
                continue;
            }

            parts.Add($"{key}: req={FormatValue(value.Required)}, cur={FormatValue(value.Current)}, miss={FormatValue(value.Missing)}, prod/h={FormatProduction(value.ProductionPerHour)}, wait_s={FormatWait(value.WaitSeconds)}, reason={value.WaitReason}");
        }

        // Clear human-readable headline first, then the per-resource diagnostics for debugging.
        var headline = string.Equals(snapshot.WaitReason, "storage_capacity", StringComparison.OrdinalIgnoreCase)
            ? $"Storage capacity too low for {FriendlyUpgradeTarget(snapshot.BlockedLabel)} ({snapshot.StorageCapacityKind ?? "storage"}). Waiting {snapshot.WaitSeconds}s."
            : $"Not enough resources to build {FriendlyUpgradeTarget(snapshot.BlockedLabel)}. Waiting {snapshot.WaitSeconds}s.";
        var details = parts.Count > 0 ? $" | {string.Join(" | ", parts)}" : string.Empty;
        return $"{headline}{details} | wait_reason={snapshot.WaitReason}";
    }

    // Turns an internal blocked label ("Building slot 19 (Warehouse) upgrade to level 7",
    // "Resource slot 9 (Cropland) upgrade to level 10", "Building slot 31 construct Marketplace") into a
    // short "Name level N" / "Name" phrase for the user-facing wait log.
    private static string FriendlyUpgradeTarget(string blockedLabel)
    {
        if (string.IsNullOrWhiteSpace(blockedLabel))
        {
            return "the building";
        }

        var nameMatch = Regex.Match(blockedLabel, @"\(([^)]+)\)");
        var name = nameMatch.Success
            ? nameMatch.Groups[1].Value.Trim()
            : blockedLabel.Trim();

        var levelMatch = Regex.Match(blockedLabel, @"level\s+(\d+)", RegexOptions.IgnoreCase);
        return levelMatch.Success ? $"{name} level {levelMatch.Groups[1].Value}" : name;
    }

    private static string BuildUpgradeResourceBlockedResultMessage(UpgradeResourceWaitSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(snapshot.BlockedLabel);
        builder.Append(": blocked by resources. ");
        builder.Append("queue_wait_seconds=");
        builder.Append(snapshot.WaitSeconds);
        builder.Append(' ');
        builder.Append(BotOptionPayloadKeys.UpgradeBlockedLabel);
        builder.Append('=');
        builder.Append(SanitizePayloadToken(snapshot.BlockedLabel));
        builder.Append(' ');
        builder.Append(BotOptionPayloadKeys.UpgradeWaitReason);
        builder.Append('=');
        builder.Append(snapshot.WaitReason);
        builder.Append(' ');
        builder.Append(BotOptionPayloadKeys.UpgradeWaitSeconds);
        builder.Append('=');
        builder.Append(snapshot.WaitSeconds);

        AppendUpgradeWaitValueTokens(builder, "wood", snapshot.Values);
        AppendUpgradeWaitValueTokens(builder, "clay", snapshot.Values);
        AppendUpgradeWaitValueTokens(builder, "iron", snapshot.Values);
        AppendUpgradeWaitValueTokens(builder, "crop", snapshot.Values);
        AppendLongToken(builder, BotOptionPayloadKeys.UpgradeWarehouseCapacity, snapshot.WarehouseCapacity);
        AppendLongToken(builder, BotOptionPayloadKeys.UpgradeGranaryCapacity, snapshot.GranaryCapacity);
        AppendStringToken(builder, BotOptionPayloadKeys.UpgradeStorageCapacityKind, snapshot.StorageCapacityKind);
        return builder.ToString();
    }

    private static void AppendUpgradeWaitValueTokens(
        System.Text.StringBuilder builder,
        string key,
        IReadOnlyDictionary<string, UpgradeResourceWaitValue> values)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return;
        }

        AppendLongToken(builder, RequiredKeyFor(key), value.Required);
        AppendLongToken(builder, CurrentKeyFor(key), value.Current);
        AppendDoubleToken(builder, ProductionKeyFor(key), value.ProductionPerHour);
    }

    private static string RequiredKeyFor(string key) => key switch
    {
        "wood" => BotOptionPayloadKeys.UpgradeRequiredWood,
        "clay" => BotOptionPayloadKeys.UpgradeRequiredClay,
        "iron" => BotOptionPayloadKeys.UpgradeRequiredIron,
        _ => BotOptionPayloadKeys.UpgradeRequiredCrop,
    };

    private static string CurrentKeyFor(string key) => key switch
    {
        "wood" => BotOptionPayloadKeys.UpgradeCurrentWood,
        "clay" => BotOptionPayloadKeys.UpgradeCurrentClay,
        "iron" => BotOptionPayloadKeys.UpgradeCurrentIron,
        _ => BotOptionPayloadKeys.UpgradeCurrentCrop,
    };

    private static string ProductionKeyFor(string key) => key switch
    {
        "wood" => BotOptionPayloadKeys.UpgradeProductionWood,
        "clay" => BotOptionPayloadKeys.UpgradeProductionClay,
        "iron" => BotOptionPayloadKeys.UpgradeProductionIron,
        _ => BotOptionPayloadKeys.UpgradeProductionCrop,
    };

    private static void AppendLongToken(System.Text.StringBuilder builder, string key, long? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Value);
    }

    private static void AppendDoubleToken(System.Text.StringBuilder builder, string key, double? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendStringToken(System.Text.StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(SanitizePayloadToken(value));
    }

    private static string SanitizePayloadToken(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", "_");
    }

    /// <summary>
    /// Computes the post-action wait in milliseconds for any server speed.
    /// We always honour the page-reported duration; the 200ms minimum only kicks in when the
    /// page reports 0s (typical for 50000x / 1M servers) so the click has time to register.
    /// No upper cap — slow servers (1x, 3x) are expected to wait the full build duration.
    /// Use the cancellation token to interrupt long waits via Stop.
    /// </summary>
    private static int ComputePostActionWaitMs(int durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return 200;
        }

        // Add a small buffer so we re-read the page just after the build finishes server-side.
        return durationSeconds * 1000 + 300;
    }

    private enum BuildPageState { Other, AtMaxLevel, WorkersBusy, EmptyConstructionSlot }

    private sealed record ConstructionPageAnalysis(
        BuildPageState State,
        UpgradeAttemptResult? UpgradeActionability,
        int DurationSeconds,
        int? PopulationDelta,
        bool LooksBlockedByResources,
        string? ConstructRequirementError);

    private sealed record UpgradeResourceWaitSnapshot(
        string BlockedLabel,
        IReadOnlyDictionary<string, UpgradeResourceWaitValue> Values,
        int WaitSeconds,
        string WaitReason,
        long? WarehouseCapacity,
        long? GranaryCapacity,
        string? StorageCapacityKind);

    private sealed record UpgradeResourceWaitValue(
        long? Required,
        long? Current,
        long? Missing,
        double? ProductionPerHour,
        int? WaitSeconds,
        string WaitReason);

    private async Task<ConstructionPageAnalysis> ReadConstructionPageAnalysisAsync(
        int slotId,
        string operationLabel,
        CancellationToken cancellationToken,
        bool includeUpgradeActionability = false,
        int? constructGid = null)
    {
        var state = await DetectBuildPageStateAsync();
        var durationSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken) ?? 0;
        var populationDelta = await ReadUpgradePopulationDeltaOnCurrentPageAsync(cancellationToken);
        var looksBlockedByResources = await CurrentPageLooksBlockedByResourcesAsync(cancellationToken);
        var constructRequirementError = constructGid is int gid
            ? await ReadConstructRequirementErrorAsync(gid, cancellationToken)
            : null;
        UpgradeAttemptResult? upgradeActionability = null;
        if (includeUpgradeActionability)
        {
            upgradeActionability = await AnalyzeUpgradeActionabilityAsync(
                slotId,
                cancellationToken,
                performClick: false,
                skipNavigationIfOnExpectedSlot: true);
        }

        Notify($"[construction-analysis:verbose] {operationLabel}: slot={slotId}, state={state}, "
            + $"duration={durationSeconds}s, pop_delta={populationDelta?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
            + $"resource_blocked={looksBlockedByResources}, requirements='{constructRequirementError ?? ""}', "
            + $"actionability={upgradeActionability?.Outcome.ToString() ?? "not_read"}.");

        return new ConstructionPageAnalysis(
            state,
            upgradeActionability,
            durationSeconds,
            populationDelta,
            looksBlockedByResources,
            constructRequirementError);
    }

    private async Task<BuildingInfo?> ReadBuildingInfoForUpgradeAsync(int slotId, CancellationToken cancellationToken)
    {
        var currentPageInfo = await TryReadBuildingInfoFromCurrentBuildPageAsync(slotId, cancellationToken);
        if (currentPageInfo is not null)
        {
            Notify($"[build:verbose] slot {slotId}: using current build-page snapshot "
                + $"name='{currentPageInfo.BuildingName}', level={currentPageInfo.Level}, gid={ParseGidFromBuildingCode(currentPageInfo.BuildingCode)?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
            return currentPageInfo;
        }

        Notify($"[build:verbose] slot {slotId}: current page snapshot unavailable; reading dorf2 overview.");
        await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification on dorf2.", cancellationToken);
        var slots = await ReadBuildingInfosAsync(cancellationToken);
        return slots.TryGetValue(slotId, out var info) ? info : null;
    }

    private async Task<BuildingInfo?> TryReadBuildingInfoFromCurrentBuildPageAsync(int slotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TravianUrls.ExtractSlotIdFromUrl(_page.Url) != slotId)
        {
            return null;
        }

        try
        {
            if (await IsPageMarkedStaleAsync())
            {
                Notify($"[build:verbose] slot {slotId}: current build page is stale; dorf2 fallback required.");
                return null;
            }

            var rawJson = await _page.EvaluateAsync<string>(
                """
                ({ slotId }) => {
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
                  const slotMatch = window.location.href.match(/[?&]id=(\d+)/);
                  const currentSlot = slotMatch ? Number(slotMatch[1]) : null;
                  if (currentSlot !== slotId) {
                    return JSON.stringify({ slotId: currentSlot, hasBuildContext: false });
                  }

                  const header =
                       document.querySelector('h1.titleInHeader')
                    || document.querySelector('h1')
                    || document.querySelector('.buildingHeader h1')
                    || document.querySelector('.build_details h1');
                  const title = clean(header ? header.textContent : '');
                  const titleMatch = title.match(/\b(?:level|lvl)\s*(\d{1,3})\b/i);
                  const level = titleMatch ? Number(titleMatch[1]) : null;
                  let name = title.replace(/\b(?:level|lvl)\s*\d{1,3}\b.*$/i, '').trim();

                  const image =
                       document.querySelector('.buildingWrapper img.building')
                    || document.querySelector('.build_details img.building')
                    || document.querySelector('img.building');
                  const imageAlt = clean(image ? image.getAttribute('alt') : '');
                  if (!name && imageAlt && !/\b(?:level|lvl)\s*\d{1,3}\b/i.test(imageAlt)) {
                    name = imageAlt;
                  }

                  const gidUrlMatch = window.location.href.match(/[?&]gid=(\d{1,2})\b/i);
                  let gid = gidUrlMatch ? Number(gidUrlMatch[1]) : null;
                  if (gid === null) {
                    const gidSource = [
                      document.body ? String(document.body.className || '') : '',
                      document.querySelector('.buildingWrapper') ? String(document.querySelector('.buildingWrapper').className || '') : '',
                      image ? String(image.className || '') : ''
                    ].join(' ');
                    const gidClassMatch = gidSource.match(/\bg(\d{1,2})\b/i);
                    if (gidClassMatch) {
                      gid = Number(gidClassMatch[1]);
                    }
                  }

                  const hasBuildContext = !!(
                    header
                    || document.querySelector('.upgradeBuilding, .contract, .contractWrapper, .build_details, #contract, form[action*="build.php"]')
                  );

                  return JSON.stringify({ slotId: currentSlot, level, name, title, gid, hasBuildContext });
                }
                """,
                new { slotId });
            var snapshot = string.IsNullOrWhiteSpace(rawJson)
                ? null
                : JsonSerializer.Deserialize<CurrentBuildPageSlotSnapshot>(rawJson);
            if (snapshot is null
                || snapshot.SlotId != slotId
                || !snapshot.HasBuildContext)
            {
                return null;
            }

            var titleInfo = BuildingDomParser.ParseBuildPageTitle(snapshot.Title);
            var level = titleInfo.Level ?? snapshot.Level;
            if (level is not int currentLevel)
            {
                return null;
            }

            var buildingCode = snapshot.Gid is int gid && gid > 0
                ? $"g{gid.ToString(CultureInfo.InvariantCulture)}"
                : TryResolveBuildingCodeFromName(snapshot.Name, snapshot.Title);
            var nameCandidate = SelectBuildingNameCandidate(titleInfo.Name, snapshot.Name, snapshot.Title);
            var buildingName = ResolveBuildingDisplayName(buildingCode, nameCandidate, hasOccupancyEvidence: true);
            if (string.Equals(buildingName, "Unknown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(buildingName, "Empty", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new BuildingInfo
            {
                SlotId = slotId,
                BuildingCode = buildingCode ?? string.Empty,
                BuildingName = buildingName,
                Level = currentLevel,
                LevelKnown = true,
                HasOccupancyEvidence = true,
            };
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            Notify($"[build:verbose] slot {slotId}: current build-page snapshot hit transient navigation: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Notify($"[build:verbose] slot {slotId}: current build-page snapshot failed: {ex.Message}");
            return null;
        }
    }

    private async Task EnsureCurrentBuildPageForActionAsync(
        int slotId,
        string operationLabel,
        CancellationToken cancellationToken)
    {
        if (!TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
        {
            await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
        }
        else if (await IsPageMarkedStaleAsync())
        {
            Notify($"[build:verbose] slot {slotId}: current build page stale before action; reloading.");
            await ReloadOrGotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync($"Manual verification on slot {slotId}.", cancellationToken);
        await EnsureExpectedBuildSlotPageAsync(slotId, operationLabel, cancellationToken);
    }

    private async Task<BuildPageState> DetectBuildPageStateAsync()
    {
        try
        {
            var raw = await _page.EvaluateAsync<string>(
                """
                () => {
                  const text = (document.body.innerText || '').replace(/\s+/g, ' ').toLowerCase();
                  // WorkersBusy takes precedence — both phrases can appear simultaneously on
                  // some pages (e.g. resource sidebar may say "max"), so resolve busy first.
                  if (/all\s+(?:our\s+)?(?:workers|builders)\s+(?:are\s+)?(?:currently\s+)?busy|baumeister\s+sind\s+(?:gerade\s+)?besch[aä]ftigt|construction\s+queue\s+is\s+full/.test(text)) {
                    return 'WorkersBusy';
                  }
                  // Strict max detection: only the explicit "this building has reached its maximum level" message.
                  if (/reached\s+(?:its|the)\s+maximum\s+level|maximum\s+level\s+has\s+been\s+reached|maximalst[uü]?fe\s+erreicht/.test(text)) {
                    return 'AtMaxLevel';
                  }
                  // Empty building slot: Travian shows the construction-choice page (each constructable
                  // building wrapped in #contract_building{gid}) with no upgrade affordance. Detected
                  // structurally so it is language-independent.
                  const hasConstructChoices = !!document.querySelector('[id^="contract_building"]');
                  // NOTE: do NOT treat `.upgradeButtonsContainer` as an upgrade signal — the construct-choice
                  // page wraps every constructable building in its own `.upgradeButtonsContainer`, so its mere
                  // presence is true on empty slots too. Only the real "Upgrade to level N" affordance counts.
                  const hasUpgrade = /upgrade\s+to\s+level/i.test(document.body.innerText || '');
                  if (hasConstructChoices && !hasUpgrade) {
                    return 'EmptyConstructionSlot';
                  }
                  return 'Other';
                }
                """);
            return raw switch
            {
                "AtMaxLevel" => BuildPageState.AtMaxLevel,
                "WorkersBusy" => BuildPageState.WorkersBusy,
                "EmptyConstructionSlot" => BuildPageState.EmptyConstructionSlot,
                _ => BuildPageState.Other,
            };
        }
        catch
        {
            return BuildPageState.Other;
        }
    }

    private async Task<bool> ClickUpgradeToLevelButtonAsync(int slotId, int nextLevel, CancellationToken cancellationToken)
    {
        var pattern = new Regex($@"upgrade\s+to\s+level\s+{nextLevel}\b", RegexOptions.IgnoreCase);
        var anyPattern = new Regex(@"upgrade\s+to\s+level", RegexOptions.IgnoreCase);
        string? lastError = null;
        var selectors = new[]
        {
            ".upgradeButtonsContainer .section1 button.green.build",
            ".upgradeButtonsContainer .section1 button.green",
            ".upgradeButtonsContainer .section1 button",
            "div.addHoverClick",
            "div.button-container",
            "button.green",
            "button",
            "a.green",
            "a",
        };

        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).Filter(new LocatorFilterOptions { HasTextRegex = pattern }).First;
            if (await locator.CountAsync() > 0)
            {
                try
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = $"{selector}: {ex.Message}";
                    // Try next selector.
                }
            }
        }

        // Fallback: any element matching the broader "upgrade to level" pattern.
        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).Filter(new LocatorFilterOptions { HasTextRegex = anyPattern }).First;
            if (await locator.CountAsync() > 0)
            {
                try
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = $"{selector}: {ex.Message}";
                    // Try next selector.
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(lastError))
        {
            Notify($"[build:verbose] could not click 'Upgrade to level {nextLevel}' button "
                + $"(slot {slotId}, url '{_page.Url}'). Last click error: {lastError}");
        }
        else
        {
            // No selector matched at all — distinguish this from a click error so logs show whether the
            // button was simply absent (wrong/half-loaded page, resource-blocked state) vs present but
            // not actionable. The caller falls back to actionability analysis for the candidate detail.
            Notify($"[build:verbose] no 'Upgrade to level {nextLevel}' button matched any selector "
                + $"(slot {slotId}, url '{_page.Url}'). Falling back to actionability analysis.");
        }

        return false;
    }

    public async Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default)
    {
        using var navDiagnostics = BeginConstructionNavigationDiagnostics($"upgrade_building_to_max slot={slotId}");
        Notify($"[build] upgrade-to-max starting — slot={slotId}");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }

        var safetyCap = Math.Max(1, maxAttempts);
        var upgrades = 0;
        var transientRetries = 0;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttemptedOffers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iteration = 0; iteration < safetyCap; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {

            // Step 1: read current level + figure out max from catalog. Prefer the current
            // build.php?id=N page when it can prove the slot snapshot, otherwise fall back to dorf2.
            var info = await ReadBuildingInfoForUpgradeAsync(slotId, cancellationToken);
            if (info is null)
            {
                return $"Slot {slotId}: not found on dorf2. Upgrades performed: {upgrades}.";
            }
            var currentLevel = info.Level;
            var gid = ParseGidFromBuildingCode(info.BuildingCode);
            var maxLevel = gid is int g ? BuildingCatalogService.MaxLevelFor(g) : 20;
            var buildingName = string.IsNullOrWhiteSpace(info.BuildingName) ? $"slot {slotId}" : info.BuildingName;
            if (currentLevel >= maxLevel)
            {
                return $"Slot {slotId}: already at max level {maxLevel}. Upgrades performed: {upgrades}.";
            }
            var highestKnownLevel = await ReadHighestKnownQueuedBuildingLevelAsync(buildingName, currentLevel, cancellationToken);
            if (highestKnownLevel >= maxLevel)
            {
                var queuedWaitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, 0, cancellationToken);
                return $"Slot {slotId}: upgrade toward max already queued and still in progress (max {maxLevel}). Upgrades performed: {upgrades}. queue_wait_seconds={queuedWaitSeconds}";
            }
            var nextLevel = highestKnownLevel + 1;
            var buildQueueBefore = await ReadBuildQueueAsync(cancellationToken);

            // Tribe/Plus-aware slot gate: if the build queue is full, defer this task back to
            // the program queue (queue_wait_seconds) rather than blocking the worker thread.
            var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, upgrades, cancellationToken);
            if (deferMessage is not null)
            {
                return deferMessage;
            }

            // Step 3: open or reuse the slot's build page.
            await EnsureCurrentBuildPageForActionAsync(slotId, "upgrade-to-max", cancellationToken);
            // Wait for the build slot controls to render before reading/clicking (see UpgradeBuildingToLevelAsync).

            // Step 4: read upgrade duration so we know how long to wait.
            var pageAnalysis = await ReadConstructionPageAnalysisAsync(slotId, "upgrade-to-max pre-click", cancellationToken);
            var durationSeconds = pageAnalysis.DurationSeconds;
            // Read the population increase this level grants before clicking (page changes after).
            var populationDelta = pageAnalysis.PopulationDelta;

            // Step 5: click "Upgrade to level N".
            var clicked = await TryUseConstructFasterForBuildAsync(
                slotId,
                gid,
                buildingName,
                currentLevel,
                nextLevel,
                buildQueueBefore,
                durationSeconds,
                null,
                cancellationToken);
            var usedConstructFasterVideo = clicked;
            if (!clicked)
            {
                clicked = await ClickUpgradeToLevelButtonAsync(slotId, nextLevel, cancellationToken);
            }
            if (!clicked)
            {
                pageAnalysis = await ReadConstructionPageAnalysisAsync(
                    slotId,
                    "upgrade-to-max no-click",
                    cancellationToken,
                    includeUpgradeActionability: true);
                var state = pageAnalysis.State;
                if (state == BuildPageState.AtMaxLevel)
                {
                    return $"Slot {slotId}: at max level (page reports max). Upgrades performed: {upgrades}.";
                }
                if (state == BuildPageState.WorkersBusy)
                {
                    Notify($"Slot {slotId}: workers busy, waiting 2s before retry. Upgrades so far: {upgrades}.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }
                if (state == BuildPageState.EmptyConstructionSlot)
                {
                    return $"Slot {slotId} is empty — construct the building before upgrading "
                        + $"(page shows a construction menu, not an 'Upgrade to level {nextLevel}' button). "
                        + $"Upgrades performed: {upgrades}.";
                }

                var actionability = pageAnalysis.UpgradeActionability ?? new UpgradeAttemptResult(
                    UpgradeAttemptOutcome.BlockedUnknown,
                    "Construction page analysis did not return upgrade actionability.",
                    null,
                    null,
                    null,
                    null,
                    string.Empty);

                // The current build page already has the exact resource block and hero-transfer control.
                // Use it before any dorf2 queue probe so a normal top-up does not navigate away and back.
                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources
                    && pageAnalysis.LooksBlockedByResources)
                {
                    var offerLevel = actionability.DetectedTargetLevel ?? nextLevel;
                    var offerCost = await TryReadLiveResourceUpgradeCostOnCurrentPageAsync(cancellationToken);
                    var offerKey = BuildConstructionHeroTransferOfferKey(slotId, offerLevel, offerCost);
                    if (heroTransferAttemptedOffers.Add(offerKey))
                    {
                        var label = $"Building slot {slotId} ({buildingName}) upgrade to level {offerLevel}";
                        Notify($"[build] hero-transfer offer key={offerKey} label='{label}'.");
                        if (await TryHeroResourceTransferForConstructionAsync(label, cancellationToken))
                        {
                            // The transfer topped up resources and reloaded the build page. Retry construct-faster
                            // before falling back to the normal button; resource shortage can disable the video
                            // button until this point.
                            await EnsureExpectedBuildSlotPageAsync(slotId, "upgrade after hero transfer", cancellationToken);
                            await ActionPacer.FromOptions(_config, Notify).DelayAsync(
                                _config.ActionPacingPageLoadMinSeconds,
                                _config.ActionPacingPageLoadMaxSeconds,
                                cancellationToken,
                                "after hero transfer reload");
                            clicked = await TryUseConstructFasterForBuildAsync(
                                slotId,
                                gid,
                                buildingName,
                                currentLevel,
                                nextLevel,
                                buildQueueBefore,
                                durationSeconds,
                                null,
                                cancellationToken);
                            if (!clicked)
                            {
                                clicked = await ClickUpgradeToLevelButtonAsync(slotId, nextLevel, cancellationToken);
                            }
                        }
                    }
                }

                if (!clicked)
                {
                    // Defense-in-depth for ambiguous/failed transfer states: verify queue capacity before
                    // NPC trade or defer classification. This probe may navigate to dorf2, so restore the
                    // slot page afterwards for the remaining build-page reads.
                    var queueDefer = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, upgrades, cancellationToken);
                    if (queueDefer is not null)
                    {
                        return queueDefer;
                    }
                    await EnsureExpectedBuildSlotPageAsync(slotId, "resource recheck after queue gate", cancellationToken);
                }

                if (!clicked && !constructionNpcTradeAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    constructionNpcTradeAttempted = true;
                    if (await TryNpcTradeForConstructionAsync($"Building slot {slotId} ({buildingName}) upgrade to level {actionability.DetectedTargetLevel ?? nextLevel}", cancellationToken))
                    {
                        continue;
                    }
                }

                // Still not clicked (transfer/NPC didn't unblock it) → classify and defer.
                if (!clicked)
                {
                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                    {
                        var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                            $"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}",
                            UpgradeMath.ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                            cancellationToken);
                        return BuildUpgradeResourceBlockedResultMessage(snapshot);
                    }
                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByQueue)
                    {
                        var waitSeconds = UpgradeMath.ClampResourceWaitSeconds(actionability.QueueWaitSeconds);
                        // Always defer queue waits (release the browser; the queue retries the item).
                        // A zero wait means the queue should already be free — retry in place.
                        if (waitSeconds > 0)
                        {
                            return $"Slot {slotId} blocked by queue. queue_wait_seconds={waitSeconds}";
                        }
                        continue;
                    }
                    var candidateSummary = string.IsNullOrWhiteSpace(actionability.DebugSummary) ? "none" : actionability.DebugSummary;
                    return $"Slot {slotId}: could not find 'Upgrade to level {nextLevel}' button. "
                        + $"Reason: {actionability.Outcome} ({actionability.Reason}). "
                        + $"url='{_page.Url}' candidates=[{candidateSummary}]. "
                        + $"Upgrades performed: {upgrades}.";
                }
            }

            upgrades += 1;
            var constructFasterResultNote = usedConstructFasterVideo ? " 25% faster (video)." : string.Empty;
            Notify($"[build] {(usedConstructFasterVideo ? "25% faster video ok" : "click ok")} — slot={slotId} '{buildingName}' lvl {currentLevel} → {nextLevel} queued (duration~{durationSeconds}s, pop +{populationDelta?.ToString() ?? "?"})");
            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }
            await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
            var postClickWaitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, durationSeconds, cancellationToken);
            transientRetries = 0;
            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, currentLevel, buildingName, buildQueueBefore, gid, nextLevel, cancellationToken);
            if (!progress.Advanced && !progress.QueuedOrInProgress)
            {
                // Final dorf2 probe before deferring — see UpgradeBuildingToLevelAsync for rationale.
                var dorf2Level = await ProbeSlotLevelOnDorf2Async(slotId, cancellationToken);
                if (dorf2Level is int confirmedLevel && confirmedLevel > currentLevel)
                {
                    upgrades += 1;
                    Notify($"Slot {slotId}: upgrade-to-max confirmed via dorf2 level read ({currentLevel} → {confirmedLevel}).");
                    if (confirmedLevel >= maxLevel)
                    {
                        return $"Slot {slotId}: reached max level {maxLevel}. Upgrades performed: {upgrades}.{constructFasterResultNote}";
                    }
                    continue;
                }

                var waitMs = ComputePostActionWaitMs(durationSeconds);
                var waitSeconds = Math.Max(1, (int)Math.Ceiling(waitMs / 1000d));
                Notify($"Slot {slotId}: upgrade-to-max click did not confirm immediately ({progress.Evidence}). Deferring {waitSeconds}s before retry.");
                return $"Slot {slotId}: upgrade-to-max click did not confirm immediately ({progress.Evidence}). queue_wait_seconds={waitSeconds}";
            }

            if (progress.Advanced)
            {
                Notify($"[build] level advance confirmed — slot={slotId} '{buildingName}' now lvl {nextLevel} (max {maxLevel}, upgrades this run: {upgrades})");
                if (nextLevel >= maxLevel)
                {
                    return $"Slot {slotId}: reached max level {maxLevel}. Upgrades performed: {upgrades}.{constructFasterResultNote}";
                }

                continue;
            }

            if (nextLevel < maxLevel)
            {
                continue;
            }

            return $"Slot {slotId}: upgrade toward max queued and still in progress (next level {nextLevel}, max {maxLevel}). Upgrades performed: {upgrades}.{constructFasterResultNote} queue_wait_seconds={postClickWaitSeconds}";

            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex) && transientRetries < 6)
            {
                transientRetries += 1;
                Notify($"UpgradeBuildingToMaxAsync slot={slotId} hit transient navigation error ({transientRetries}/6): {ex.Message}. Retrying...");
                await Task.Delay(400 * transientRetries, cancellationToken);
            }
        }

        return $"Slot {slotId}: hit safety cap of {safetyCap} iterations. Upgrades performed: {upgrades}.";
    }

    public async Task<string> ConstructBuildingAsync(int slotId, int gid, string name, CancellationToken cancellationToken = default)
    {
        using var navDiagnostics = BeginConstructionNavigationDiagnostics($"construct_building slot={slotId} gid={gid}");
        Notify($"[construct] starting — slot={slotId}, gid={gid}");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }
        if (gid <= 0)
        {
            throw new InvalidOperationException("Building gid must be positive.");
        }

        var buildingName = string.IsNullOrWhiteSpace(name) ? $"gid {gid}" : name.Trim();
        const int safetyCap = 6;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttempted = false;

        for (var attempt = 0; attempt < safetyCap; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buildQueueBefore = await ReadBuildQueueAsync(cancellationToken);

            // Pre-flight queue gate: defer to program queue if no construction slot is free.
            var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, attempt, cancellationToken);
            if (deferMessage is not null)
            {
                return deferMessage;
            }

            // Step 1: open the slot's construction page on the right category tab so the building's
            // wrapper actually exists in the DOM. Walls (slot 40) ignore category — only one option.
            var url = Paths.BuildBySlot(slotId);
            var categoryIndex = BuildingCatalogService.CategoryIndexFor(gid);
            if (categoryIndex.HasValue && slotId != 40)
            {
                var separator = url.Contains('?') ? '&' : '?';
                url = $"{url}{separator}category={categoryIndex.Value}";
            }
            await GotoAsync(url, cancellationToken);
            await PauseForManualStepIfVisibleAsync($"Manual verification on slot {slotId} construct page.", cancellationToken);

            // Confirmed already-built guard: a stale construct task can target a slot that already holds the
            // building — e.g. a special fixed slot (Rally Point slot 39 / Wall slot 40 exist from founding)
            // or a building that appeared since the task was queued. Such a slot's build page shows the
            // building's upgrade UI, not a construct-choice page, so EnsureExpectedConstructChoicePageAsync
            // would burn retries and ALARM. Wait for the build page, then if it confirms an existing building
            // return a remove result so the queue drops the impossible task instead of failing it forever.
            await WaitForBuildSlotContextAsync(slotId, 5000, cancellationToken);
            var existingBuilding = await TryReadExistingBuildingOnSlotBuildPageAsync(slotId);
            if (existingBuilding is { Level: >= 1 } built)
            {
                Notify($"[construct] slot {slotId} already holds '{built.Name}' level {built.Level} — confirmed already built; removing task from queue.");
                return $"Construct skipped: {buildingName} already exists at slot {slotId} (confirmed '{built.Name}' level {built.Level}). Removing from queue.";
            }

            // Server-appended gid guard: on Official, build.php?id=N for an OCCUPIED slot redirects to
            // ...&gid=<existing building>. The bot never puts gid= in the construct url itself, so a
            // matching gid here proves the slot already holds this building — typically level 0 because
            // an earlier click landed but its confirmation was missed, so the level>=1 guard above does
            // not fire. The construct-choice page will never load in that state; defer until the
            // construction completes instead of burning retries into an ALARM.
            if (existingBuilding is null)
            {
                var slotOccupiedByRequestedGid = false;
                try
                {
                    slotOccupiedByRequestedGid = await _page.EvaluateAsync<bool>(
                        """
                        ({ slotId, gid }) => {
                          const url = window.location.href;
                          if (!/build\.php/i.test(url)) return false;
                          const idMatch = url.match(/[?&]id=(\d+)/);
                          if (!idMatch || Number(idMatch[1]) !== slotId) return false;
                          const gidMatch = url.match(/[?&]gid=(\d+)/);
                          if (!gidMatch || Number(gidMatch[1]) !== gid) return false;
                          // A construct-choice page offers contracts; an occupied slot's page does not.
                          return !document.querySelector('[id^="contract_building"], #contract_building');
                        }
                        """,
                        new { slotId, gid });
                }
                catch (Exception ex) when (IsTransientExecutionContextException(ex))
                {
                    Notify($"[construct] slot {slotId} occupied-gid check hit transient navigation: {ex.Message}");
                }

                if (slotOccupiedByRequestedGid)
                {
                    var waitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, 60, cancellationToken);
                    Notify($"[construct] slot {slotId} already holds gid {gid} ({buildingName}), still under construction — deferring {waitSeconds}s until it completes.");
                    return $"Slot {slotId}: {buildingName} construction already in progress (slot already holds gid {gid}). queue_wait_seconds={waitSeconds}";
                }
            }

            await EnsureExpectedConstructChoicePageAsync(slotId, gid, url, "construct", cancellationToken);

            // Step 2: read build page state, duration and population from one page analysis.
            var pageAnalysis = await ReadConstructionPageAnalysisAsync(
                slotId,
                "construct pre-click",
                cancellationToken,
                constructGid: gid);
            var durationSeconds = pageAnalysis.DurationSeconds;
            // Read the population the new building grants before clicking (page changes after).
            var populationDelta = pageAnalysis.PopulationDelta;

            // Step 3: click the "Construct building" button (scoped to this gid when possible).
            var clicked = await TryUseConstructFasterForBuildAsync(
                slotId,
                gid,
                buildingName,
                0,
                1,
                buildQueueBefore,
                durationSeconds,
                url,
                cancellationToken);
            var usedConstructFasterVideo = clicked;
            if (!clicked)
            {
                clicked = await ClickConstructBuildingButtonAsync(gid, cancellationToken);
            }
            if (!clicked)
            {
                // Classify the construct page before any queue/progress check navigates to dorf2.
                // Otherwise a normal resource block is read from the wrong page and degrades into the
                // misleading "could not find Construct building button" alarm.
                pageAnalysis = await ReadConstructionPageAnalysisAsync(
                    slotId,
                    "construct no-click",
                    cancellationToken,
                    constructGid: gid);
                var blockedByResources = pageAnalysis.LooksBlockedByResources;
                var missingRequirements = pageAnalysis.ConstructRequirementError;
                if (!string.IsNullOrWhiteSpace(missingRequirements))
                {
                    // Requirement errors are more specific than resource hints on Official construct
                    // pages. A soon-available building can still show resource rows, but hero transfer/NPC
                    // cannot make it buildable until the prerequisite exists.
                    var waitSeconds = UpgradeMath.ClampResourceWaitSeconds(null);
                    Notify($"Slot {slotId}: {buildingName} not buildable yet — missing {missingRequirements}.");
                    return $"Slot {slotId}: {buildingName} cannot be built yet. Missing requirements: {missingRequirements}. Upgrades performed: 0. queue_wait_seconds={waitSeconds}";
                }

                if (blockedByResources)
                {
                    if (!heroTransferAttempted)
                    {
                        heroTransferAttempted = true;
                        if (await TryHeroResourceTransferForConstructionAsync($"Building slot {slotId} construct {buildingName}", cancellationToken))
                        {
                            continue;
                        }
                    }

                    // The construct page has the exact resource block and hero-transfer control. Only
                    // navigate to dorf2 after that direct attempt, matching the upgrade flow and avoiding
                    // a false "No hero transfer offered" result from probing the overview page.
                    var queueDefer = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, attempt, cancellationToken);
                    if (queueDefer is not null)
                    {
                        return queueDefer;
                    }

                    var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Building slot {slotId} construct {buildingName}",
                        60,
                        cancellationToken);

                    if (!constructionNpcTradeAttempted)
                    {
                        constructionNpcTradeAttempted = true;
                        if (await TryNpcTradeForConstructionAsync($"Building slot {slotId} construct {buildingName}", cancellationToken))
                        {
                            continue;
                        }
                    }

                    return BuildUpgradeResourceBlockedResultMessage(snapshot);
                }

                var waitAfterBusy = await WaitForConstructionSlotIfBusyAsync(ConstructionKind.Building, cancellationToken);
                if (waitAfterBusy > 0)
                {
                    continue;
                }

                var existingProgress = await DetectConstructProgressAsync(slotId, gid, buildingName, buildQueueBefore, 1, cancellationToken);
                if (existingProgress.Started)
                {
                    return $"Queued {buildingName} in slot {slotId}. Evidence: {existingProgress.Evidence}.";
                }

                // Queue/progress verification navigates away from the construct page. Re-open and verify
                // the exact slot/category before one final click attempt, covering transient redirects or
                // a construct page that had not finished rendering on the first scan.
                await EnsureExpectedConstructChoicePageAsync(
                    slotId,
                    gid,
                    url,
                    "construct retry",
                    cancellationToken);
                clicked = await ClickConstructBuildingButtonAsync(gid, cancellationToken);
                if (clicked)
                {
                    Notify($"Slot {slotId}: construct button for gid {gid} found on verified retry.");
                }
            }

            if (!clicked)
            {
                await CaptureFailureArtifactsAsync($"construct-slot-{slotId}-gid-{gid}-no-click", cancellationToken);
                return $"Slot {slotId}: verified construct page but could not find an actionable 'Construct building' button for gid {gid}.";
            }

            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }

            var constructFasterResultNote = usedConstructFasterVideo ? " 25% faster (video)." : string.Empty;
            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, 0, buildingName, buildQueueBefore, gid, 1, cancellationToken);
            if (!progress.Advanced && !progress.QueuedOrInProgress)
            {
                // Final dorf2 probe: an instant-build server can finish a level-1 construct
                // before the queue ever shows it; any visible level > 0 means the click landed.
                var dorf2Level = await ProbeSlotLevelOnDorf2Async(slotId, cancellationToken);
                if (dorf2Level is int confirmedLevel && confirmedLevel >= 1)
                {
                    return $"Constructed {buildingName} in slot {slotId} (confirmed level {confirmedLevel} on dorf2).{constructFasterResultNote}";
                }

                var waitMs = ComputePostActionWaitMs(durationSeconds);
                var waitSeconds = Math.Max(1, (int)Math.Ceiling(waitMs / 1000d));
                Notify($"Slot {slotId}: construct click did not confirm immediately ({progress.Evidence}). Deferring {waitSeconds}s before retry.");
                return $"Slot {slotId}: construct click did not confirm immediately ({progress.Evidence}). queue_wait_seconds={waitSeconds}";
            }

            return $"Queued {buildingName} in slot {slotId}. Evidence: {progress.Evidence}.{constructFasterResultNote}";
        }

        return $"Slot {slotId}: hit safety cap while trying to queue {buildingName}.";
    }

    private async Task<(bool Started, string Evidence)> DetectConstructProgressAsync(
        int slotId,
        int gid,
        string buildingName,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int targetLevel,
        CancellationToken cancellationToken)
    {
        try
        {
            var queueItems = await ReadBuildQueueAsync(cancellationToken);
            var queueFingerprintBefore = BuildQueueFingerprints.Identity(buildQueueBefore);
            var queueFingerprintAfter = BuildQueueFingerprints.Identity(queueItems);
            var targetQueueItem = BuildQueueFingerprints.FindNewTargetBuilding(buildQueueBefore, queueItems, buildingName, slotId, gid, targetLevel)
                ?? BuildQueueFingerprints.FindTargetBuilding(queueItems, buildingName, slotId, gid, targetLevel);
            if (targetQueueItem is not null)
            {
                return (true, $"build queue contains slot {targetQueueItem.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} {buildingName}");
            }

            var newQueueItem = BuildQueueFingerprints.FindNewBuildingByName(buildQueueBefore, queueItems, buildingName);
            if (newQueueItem is not null)
            {
                return (true, $"build queue added {buildingName}");
            }

            if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
            {
                Notify($"Construct progress check for slot {slotId}: queue changed but no {buildingName} entry was found.");
            }

            var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken);
            var matchingActiveConstruction = activeConstructions.FirstOrDefault(item =>
                item.Kind != ConstructionKind.Resource
                && ActiveConstructionMatchesTarget(item, slotId, gid, targetLevel));
            if (matchingActiveConstruction is not null)
            {
                return (true, $"active construction detected for slot {matchingActiveConstruction.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} {matchingActiveConstruction.Name}");
            }

            await GotoAsync(Paths.Buildings, cancellationToken);
            await PauseForManualStepIfVisibleAsync($"Manual verification while verifying slot {slotId} construction state.", cancellationToken);
            var slots = await ReadBuildingInfosAsync(cancellationToken);
            if (slots.TryGetValue(slotId, out var slotInfo))
            {
                var slotGid = ParseGidFromBuildingCode(slotInfo.BuildingCode);
                var sameBuilding = slotGid == gid || BuildingNames.Same(slotInfo.BuildingName, buildingName);
                if (sameBuilding && slotInfo.Level >= 0)
                {
                    var slotLabel = string.IsNullOrWhiteSpace(slotInfo.BuildingName) ? buildingName : slotInfo.BuildingName;
                    return (true, $"slot {slotId} now shows {slotLabel} level {slotInfo.Level}");
                }
            }
        }
        catch (Exception ex)
        {
            Notify($"Construct progress verification for slot {slotId} skipped: {ex.Message}");
        }

        return (false, "no queue or construction evidence");
    }

    private async Task<bool> ClickConstructBuildingButtonAsync(int gid, CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notify($"Construct page load wait failed before button scan: {ex.Message}");
            // Continue regardless; we'll still look for the button below.
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => /\\bconstruct(?:\\s+building)?\\b|\\bbuild(?:\\s+building)?\\b|\\bbauen\\b|\\bbygg\\b|\\bcostruisci\\b/i.test(document.body.innerText || '')",
                null,
                new PageWaitForFunctionOptions { Timeout = 15000 })
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Notify($"Construct page readiness wait failed before button scan: {ex.Message}");
            // Continue anyway.
        }

        var rawJson = await _page.EvaluateAsync<string>(
            """
            ({ gid }) => {
              const gidText = String(gid);
              const candidates = Array.from(document.querySelectorAll(
                'button, input[type="submit"], input[type="button"], a, div.addHoverClick, div.button-container'
              ));
              const seen = [];
              const matches = [];
              // Match `?a=N`, `&a=N`, `?gid=N`, `&gid=N`, plus Cyrillic 'а' (U+0430) as a tolerant fallback.
              const otherGidRe = /[?&](?:[aа]|gid)=(\d+)/gi;
              const constructActionRe = /\bconstruct(?:\s+building)?\b|\bbuild(?:\s+building)?\b|\bbauen\b|\bbygg\b|\bcostruisci\b/i;
              for (const el of candidates) {
                const rawText = (el.textContent || '').replace(/\s+/g, ' ').trim();
                const value = (el.getAttribute('value') || '').replace(/\s+/g, ' ').trim();
                const actionText = `${rawText} ${value}`.replace(/\s+/g, ' ').trim();
                const text = actionText.toLowerCase();
                const classes = (el.className || '').toString().toLowerCase();
                const seenText = rawText || value;
                if (seenText) seen.push({ text: seenText.slice(0, 60), classes: classes.slice(0, 60) });
                if (!constructActionRe.test(actionText)) continue;
                const disabled = el.disabled || classes.includes('disabled') || el.getAttribute('aria-disabled') === 'true';
                if (disabled) continue;
                const inOfficialPrimarySection = !!el.closest('.upgradeButtonsContainer .section1');
                const inOfficialSpeedupSection = !!el.closest('.upgradeButtonsContainer .section2');
                if (text.includes('npc') || text.includes('instant') || text.includes('faster') || classes.includes('gold') || classes.includes('purple') || classes.includes('videofeaturebutton') || inOfficialSpeedupSection) continue;
                const isUpgrade = /upgrade\s+to\s+level/i.test(text);
                if (isUpgrade) continue;
                // Travian wraps each constructable building in `#contract_building{gid}`; search broadly for fallbacks.
                const wrapper = el.closest(
                  `#contract_building${gidText}, #building${gidText}, [id$="_building${gidText}"], [data-gid="${gidText}"], .gid${gidText}`
                );
                const wrapperMatchesGid = wrapper !== null;
                const onclick = (el.getAttribute('onclick') || '');
                const href = (el.getAttribute('href') || '');
                const combined = `${onclick} ${href} ${value}`.toLowerCase();
                const onclickMentionsGid =
                  combined.includes(`gid=${gidText}`)
                  || combined.includes(`gid%3d${gidText}`)
                  || combined.includes(`a=${gidText}`)
                  || combined.includes(`а=${gidText}`)         // Cyrillic 'а' literal
                  || combined.includes(`%d0%b0=${gidText}`);        // Cyrillic 'а' URL-encoded
                // If the click URL or wrapper references a DIFFERENT gid, skip — never click a foreign building.
                otherGidRe.lastIndex = 0;
                let mentionsForeignGid = false;
                let m;
                while ((m = otherGidRe.exec(combined)) !== null) {
                  if (m[1] && m[1] !== gidText) { mentionsForeignGid = true; break; }
                }
                if (mentionsForeignGid && !onclickMentionsGid && !wrapperMatchesGid) continue;
                // Skip if button lives inside a wrapper that explicitly belongs to a different building.
                const otherWrapper = el.closest('[id^="contract_building"], [id^="building"]');
                if (otherWrapper && wrapper && otherWrapper !== wrapper) continue;
                if (otherWrapper && !wrapper) {
                  const otherId = (otherWrapper.id || '').toLowerCase();
                  if (otherId !== `contract_building${gidText}` && otherId !== `building${gidText}`) continue;
                }
                if (!wrapperMatchesGid && !onclickMentionsGid) continue;
                const isConstruct = constructActionRe.test(actionText);
                const isDirectAction = el.matches('button, input[type="submit"], input[type="button"], a, div.addHoverClick');
                const rank = (wrapperMatchesGid ? 10 : 0) + (inOfficialPrimarySection ? 8 : 0) + (onclickMentionsGid ? 5 : 0) + (isDirectAction ? 4 : 0) + (isConstruct ? 3 : 0) + (classes.includes('green') ? 2 : 0) + 1;
                matches.push({ index: candidates.indexOf(el), rank, text: actionText.slice(0, 60), gidContext: { wrapper: wrapperMatchesGid, onclick: onclickMentionsGid } });
              }
              matches.sort((a, b) => b.rank - a.rank);
              const best = matches.length > 0 ? matches[0] : null;
              const bestEl = best ? candidates[best.index] : null;
              return JSON.stringify({ clicked: false, clickIndex: best ? best.index : null, clickId: bestEl && bestEl.id ? bestEl.id : '', matches: matches.slice(0, 5), seen: seen.slice(0, 20) });
            }
            """,
            new { gid });

        Notify($"Construct candidate scan: {rawJson}");
        try
        {
            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;

            var clickId = root.TryGetProperty("clickId", out var clickIdProp)
                && clickIdProp.ValueKind == JsonValueKind.String
                    ? clickIdProp.GetString()
                    : null;
            int? clickIndex = root.TryGetProperty("clickIndex", out var clickIndexProp)
                && clickIndexProp.ValueKind == JsonValueKind.Number
                && clickIndexProp.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : null;

            if (string.IsNullOrWhiteSpace(clickId) && clickIndex is null)
            {
                return false;
            }

            // Prefer the matched element's stable id. The scan computes its position via
            // document.querySelectorAll, but Playwright's CSS engine pierces open shadow DOM (cookie
            // consent / React widgets) and runs on a separate snapshot, so a positional Nth(index) can
            // resolve to a different — often hidden — element and hang ClickAsync for the full timeout.
            // Pinning the exact button by id avoids that misalignment; Nth stays as a last-resort fallback
            // for the rare candidate that has no id.
            var clickTarget = !string.IsNullOrWhiteSpace(clickId)
                ? _page.Locator($"[id=\"{clickId}\"]").First
                : _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(clickIndex!.Value);

            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            try
            {
                await clickTarget.ClickAsync(new LocatorClickOptions
                {
                    Timeout = _config.TimeoutMs,
                });
                return true;
            }
            catch (Exception clickEx) when (!string.IsNullOrWhiteSpace(clickId))
            {
                // The target button is known and gid-scoped. If a normal click cannot reach it (e.g. an
                // overlay intercepts pointer events), dispatch the element's own click handler. Its onclick
                // navigates via window.location.href, so this performs the same construction action.
                Notify($"Construct click on '{clickId}' fell back to scripted click: {clickEx.Message}");
                return await _page.EvaluateAsync<bool>(
                    """
                    (id) => {
                      const el = document.getElementById(id);
                      if (!el) return false;
                      el.click();
                      return true;
                    }
                    """,
                    clickId);
            }
        }
        catch (Exception ex)
        {
            Notify($"Could not click construct candidate for gid {gid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// On the construct-choice page Official renders unmet prerequisites for a building as
    /// <c>span.buildingCondition.error</c> elements (e.g. "Main Building Level 3") inside that gid's
    /// <c>#contract_building{gid}</c> wrapper, with no 'Construct building' button. Returns the joined
    /// requirement text (e.g. "Main Building Level 3, Academy Level 1") or null when none is present.
    /// </summary>
    private async Task<string?> ReadConstructRequirementErrorAsync(int gid, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _page.EvaluateAsync<string>(
                """
                ({ gid }) => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const wrapper = document.querySelector(`#contract_building${gid}`);
                  if (!wrapper) return '';
                  // A real construct button means it IS buildable — no requirement error to report.
                  if (wrapper.querySelector('button[value="Construct building"], button.green.new')) return '';
                  const conditions = Array.from(wrapper.querySelectorAll('.buildingCondition.error'))
                    .map((node) => clean(node.textContent))
                    .filter((text) => text.length > 0);
                  return conditions.join(', ');
                }
                """,
                new { gid });
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Improves only the user-selected troops at the Smithy, each up to its own target level. Reads every
    /// troop row, classifies it (improvable / already at target / maxed / no resources / queue busy / not
    /// researched), clicks Improve for an available targeted troop, and defers on the live queue timer when
    /// the smithy is busy. Rows are identified by unit id (img.unit.uNN) or troop slot (t=tN),
    /// never by button text alone.
    /// </summary>
    public async Task<string> UpgradeSelectedTroopsAtSmithyAsync(
        IReadOnlyList<SmithyTroopTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var targetList = targets ?? [];
        Notify($"UpgradeSelectedTroopsAtSmithyAsync started with {targetList.Count} target(s): "
            + string.Join(", ", targetList.Select(t => $"{t.Key}->{t.TargetLevel}")));
        if (targetList.Count == 0)
        {
            return "Smithy: no troops selected for upgrade.";
        }

        var smithySlotId = await TryResolveSmithySlotIdAsync(cancellationToken);
        if (!smithySlotId.HasValue)
        {
            return "Smithy not found in this village. Build a Smithy first.";
        }
        Notify($"Smithy found at slot {smithySlotId.Value}.");

        // Travian Plus grants a second concurrent Smithy research slot (same idea as the second build queue
        // slot for construction). Read it once so the loop greedily fills BOTH slots before deferring,
        // instead of stopping after one. Unknown Plus is treated as 1 slot (conservative, never over-fills).
        var (_, smithyPlusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        var maxConcurrentUpgrades = smithyPlusActive ? 2 : 1;
        Notify($"Smithy: Plus={smithyPlusActive}; max concurrent upgrades={maxConcurrentUpgrades}.");

        // Targets still needing a decision, keyed by their identity (dedupes duplicate selections).
        var pending = new Dictionary<string, SmithyTroopTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targetList)
        {
            if (target is not null && !string.IsNullOrWhiteSpace(target.Key))
            {
                pending[target.Key] = target;
            }
        }

        const int safetyCap = 60;
        // Re-check interval when the page gives no exact ETA (e.g. resource shortage without a countdown).
        const int DefaultRecheckSeconds = 300;
        var improved = 0;
        var skipped = 0;
        var consecutiveEmptyReloads = 0;
        var consecutiveZeroDurationReloads = 0;
        // A slot is free but the page showed no ready Improve button (usually a React re-render race right
        // after starting a research). Bounded reloads to fill the free slot before giving up.
        var consecutiveFreeSlotStallReads = 0;
        // Last Smithy research queue we reported to the dashboard ("[smithy-queue]"), to emit on change only.
        string? lastQueueCsv = null;
        // Troops we already tried to top up from the hero this run — bounds hero transfers to one attempt
        // per troop so a partial/ineffective transfer can never drain the hero in a loop.
        var heroTransferAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iter = 0; iter < safetyCap && pending.Count > 0; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var smithyPath = Paths.BuildBySlot(smithySlotId.Value);
            if (!IsCurrentUrlForPath(smithyPath))
            {
                await GotoAsync(smithyPath, cancellationToken);
            }
            try
            {
                // Official Smithy is React-rendered; wait for the page to be actionable (not just DOM-present)
                // so the very first read sees real buttons/resources and can improve immediately when possible.
                await WaitForPageReadyAsync(cancellationToken);
            }
            catch
            {
                // Continue; the row read below is retry-wrapped.
            }
            await Task.Delay(400, cancellationToken);

            // The smithy itself may need an upgrade before more troop levels can be researched.
            var needsSmithyUpgrade = await RetryAsync(
                "Smithy: detect 'improve the blacksmith'",
                () => _page.EvaluateAsync<bool>(
                    "() => /improve\\s+the\\s+(blacksmith|smithy)/i.test(document.body.innerText || '')"),
                cancellationToken: cancellationToken);
            if (needsSmithyUpgrade)
            {
                Notify($"Smithy capacity exhausted (\"Improve the blacksmith\" detected). Building slot {smithySlotId.Value}.");
                var upgradeResult = await UpgradeBuildingToMaxAsync(smithySlotId.Value, cancellationToken: cancellationToken);
                Notify($"Smithy build result: {upgradeResult}");
                consecutiveEmptyReloads = 0;
                consecutiveZeroDurationReloads = 0;

                // If the construction queue is full/busy, the Smithy can't grow right now. Defer on that
                // timer (no point retrying while the build queue is full) so the task comes back when the
                // building finishes — without blocking the other loop groups.
                if (TryReadQueueWaitSeconds(upgradeResult, out var buildWaitSeconds))
                {
                    Notify($"Smithy: build queue busy, deferring {buildWaitSeconds}s before improving more troops.");
                    return $"Smithy: improved {improved}, skipped {skipped}, {pending.Count} pending. Smithy build queued. queue_wait_seconds={buildWaitSeconds}";
                }
                continue;
            }

            var rowsJson = await RetryAsync(
                "Smithy: read troop rows",
                () => ReadSmithyRowsJsonAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var rows = SmithyPageParser.ParseRows(rowsJson);
            if (rows.Count == 0)
            {
                consecutiveEmptyReloads += 1;
                if (consecutiveEmptyReloads >= 3)
                {
                    await GotoAsync(Paths.Buildings, cancellationToken);
                    return $"Smithy: no troop rows found after 3 reloads. Improved {improved}, skipped {skipped}.";
                }
                Notify($"Smithy: no troop rows visible, reload {consecutiveEmptyReloads}/3.");
                await TryReloadSmithyAsync(cancellationToken);
                continue;
            }
            consecutiveEmptyReloads = 0;

            // Report the live Smithy research queue (under_progress timers) so the dashboard shows the real
            // per-village queue/timers — emitted only when it changes to keep the log clean.
            var dashQueue = await ReadSmithyQueueEntriesAsync(cancellationToken);
            var dashQueueJson = JsonSerializer.Serialize(dashQueue);
            if (!string.Equals(dashQueueJson, lastQueueCsv, StringComparison.Ordinal))
            {
                Notify($"[smithy-queue] entries_json={dashQueueJson}");
                lastQueueCsv = dashQueueJson;
            }

            // Smithy slots currently occupied (live under_progress rows). With Plus this can be 2.
            var activeUpgradeCount = dashQueue.Count;

            // Classify every pending target. Terminal outcomes (maxed / already at target / not researched)
            // are logged and removed. Queue-busy and resource-shortage keep the troop pending and contribute
            // a wait time so the task defers and re-checks (picking up freed queue slots / incoming resources)
            // instead of skipping. An improvable troop is remembered to click.
            SmithyTroopTarget? toClick = null;
            SmithyTroopTarget? firstNoResourceTarget = null;
            var firstNoResourceLabel = string.Empty;
            var anyResearchInProgress = false;
            var anyWaitingForResources = false;
            int? minResourceWaitSeconds = null;
            foreach (var target in pending.Values.ToList())
            {
                var row = SmithyPageParser.FindRowForTarget(rows, target);
                var outcome = SmithyPageParser.Classify(row, target);
                var label = row?.Name is { Length: > 0 } rowName ? rowName : (target.Name ?? target.Key);
                switch (outcome)
                {
                    case SmithyTroopOutcome.NotResearched:
                        Notify($"Smithy: '{label}' is not listed on the Smithy page — likely not researched in the Academy yet. Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.Maxed:
                        Notify($"Smithy: '{label}' is already at max level ({SmithyPageParser.MaxLevel}). Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.AlreadyAtTarget:
                        Notify($"Smithy: '{label}' already at level {row!.CurrentLevel} (target {target.TargetLevel}). Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.SmithyLevelTooLow:
                        // Terminal: the troop is at the smithy's level cap and can't reach the requested
                        // target until the smithy building is upgraded. Skip instead of deferring forever.
                        Notify($"Smithy: '{label}' is at level {row!.CurrentLevel}; the smithy level is too low to reach target {target.TargetLevel}. Upgrade the smithy first. Skipping.");
                        pending.Remove(target.Key);
                        skipped += 1;
                        break;
                    case SmithyTroopOutcome.NoResources:
                        // Keep pending and wait — the bot re-checks and improves the troop as soon as enough
                        // resources have come in (exact ETA parsed from the page when Travian exposes it).
                        anyWaitingForResources = true;
                        if (firstNoResourceTarget is null)
                        {
                            firstNoResourceTarget = target;
                            firstNoResourceLabel = label;
                        }
                        if (row?.ResourceWaitSeconds is int rowWait && rowWait > 0)
                        {
                            minResourceWaitSeconds = minResourceWaitSeconds is int currentWait
                                ? Math.Min(currentWait, rowWait)
                                : rowWait;
                            Notify($"Smithy: '{label}' lacks resources; enough in ~{rowWait}s. Waiting.");
                        }
                        else
                        {
                            Notify($"Smithy: '{label}' lacks resources (no exact ETA on the page). Will re-check.");
                        }
                        break;
                    case SmithyTroopOutcome.InProgress:
                        anyResearchInProgress = true; // smithy busy; keep pending and defer below
                        break;
                    case SmithyTroopOutcome.Improve:
                        toClick ??= target;
                        break;
                }
            }

            if (toClick is not null)
            {
                var clicked = await RetryAsync(
                    $"Smithy: click Improve for {toClick.Key}",
                    () => ClickSmithyImproveButtonForKeyAsync(toClick.Key, cancellationToken),
                    cancellationToken: cancellationToken);
                if (clicked)
                {
                    improved += 1;
                    consecutiveZeroDurationReloads = 0;
                    Notify($"Smithy: clicked Improve for '{toClick.Name ?? toClick.Key}'. Improvements this run: {improved}.");
                    // The smithy is now busy with this research; re-evaluate (it will usually defer next).
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                Notify($"Smithy: could not click Improve for '{toClick.Key}'; will re-check after the queue frees.");
                anyResearchInProgress = true;
            }

            // Resource shortage + the user enabled hero-inventory resources: top the troop up from the hero
            // and re-evaluate (opt-in, best-effort). One attempt per troop per run so an
            // ineffective transfer can't drain the hero in a loop.
            if (toClick is null
                && firstNoResourceTarget is not null
                && _config.HeroResourceTransferEnabled
                && _config.HeroResourceUseSmithy
                && heroTransferAttempted.Add(firstNoResourceTarget.Key))
            {
                var transferred = await TryHeroResourceTransferForSmithyTroopAsync(
                    firstNoResourceTarget.Key, firstNoResourceLabel, cancellationToken);
                if (transferred)
                {
                    Notify($"Smithy: topped up '{firstNoResourceLabel}' from the hero inventory; re-checking.");
                    await Task.Delay(400, cancellationToken);
                    continue;
                }
            }

            if (pending.Count == 0)
            {
                break;
            }

            // With Plus, a research can start while another runs. A free slot means we should keep trying
            // to fill it rather than deferring on the (long) active research timer.
            var hasFreeSlot = activeUpgradeCount < maxConcurrentUpgrades;

            // Free slot but nothing was clickable, and no resource shortage explains it: the Improve buttons
            // most likely hadn't re-rendered yet after the previous click (React). Reload a few times to fill
            // the free slot before deferring, so the second Plus slot isn't left idle until the first finishes.
            if (toClick is null && hasFreeSlot && anyResearchInProgress && !anyWaitingForResources)
            {
                consecutiveFreeSlotStallReads += 1;
                if (consecutiveFreeSlotStallReads < 3)
                {
                    Notify($"Smithy: slot free ({activeUpgradeCount}/{maxConcurrentUpgrades}) but no Improve button was ready; reloading to fill it (attempt {consecutiveFreeSlotStallReads}/3).");
                    await TryReloadSmithyAsync(cancellationToken);
                    continue;
                }
            }
            else
            {
                consecutiveFreeSlotStallReads = 0;
            }

            if (anyResearchInProgress || anyWaitingForResources)
            {
                // Use the soonest concrete signal: the active research timer (queue busy) or the resource
                // ETA. When neither exposes an exact time, re-check on a moderate interval so the task picks
                // up a freed queue slot / incoming resources. Deferring never blocks the other loop groups.
                var waitCandidates = new List<int>();
                // Wait on the research-queue timer when the queue is genuinely FULL, OR when a free slot
                // could not be filled after the stall reloads above (e.g. the only pending troop is the one
                // already researching, so the free Plus slot isn't usable until it finishes). With a fillable
                // free slot we fall through to the resource ETA / moderate re-check instead of the long timer.
                if (anyResearchInProgress && (!hasFreeSlot || consecutiveFreeSlotStallReads >= 3))
                {
                    var timers = await RetryAsync(
                        "Smithy: read queue timers",
                        () => ReadSmithyQueueTimersAsync(cancellationToken),
                        cancellationToken: cancellationToken);
                    // The Smithy research queue is the source of truth: when it's full the next slot only
                    // frees when the SOONEST queued upgrade finishes, so defer on that timer (not DOM order).
                    var soonestTimer = timers.Where(t => t > 0).DefaultIfEmpty(0).Min();
                    if (soonestTimer > 0)
                    {
                        waitCandidates.Add(soonestTimer);
                    }
                }
                if (minResourceWaitSeconds is int resWait && resWait > 0)
                {
                    waitCandidates.Add(resWait);
                }

                // A concrete page timer (queue slot freeing / resources arriving) is authoritative.
                var hasConcreteWait = waitCandidates.Count > 0;
                var dur = hasConcreteWait ? waitCandidates.Min() : 0;

                // Queue busy with no readable timer can mean Travian's auto-reload stalled — reload a few
                // times before falling back to the periodic re-check.
                if (dur <= 0 && anyResearchInProgress && await IsPageMarkedStaleAsync())
                {
                    consecutiveZeroDurationReloads += 1;
                    if (consecutiveZeroDurationReloads < 3)
                    {
                        Notify($"Smithy: queue busy, timer not ready, reloading (attempt {consecutiveZeroDurationReloads}/3).");
                        await TryReloadSmithyAsync(cancellationToken);
                        continue;
                    }
                }
                consecutiveZeroDurationReloads = 0;

                if (dur <= 0)
                {
                    dur = DefaultRecheckSeconds; // no exact ETA available — re-check on a moderate interval
                    hasConcreteWait = false;
                }

                // With a real queue timer, defer on it in full (the queue stays full until then) instead of
                // waking every 10 min for nothing. Only the no-ETA fallback keeps the short periodic cap.
                var waitSec = hasConcreteWait
                    ? Math.Clamp(dur + 1, 2, 12 * 60 * 60)
                    : Math.Clamp(dur + 1, 2, 600);
                var reasonText = anyResearchInProgress && !anyWaitingForResources
                    ? "research in progress"
                    : !anyResearchInProgress && anyWaitingForResources
                        ? "waiting for resources"
                        : "queue busy / waiting for resources";
                Notify($"Smithy: {reasonText}, deferring {waitSec}s for {pending.Count} pending troop(s).");
                return $"Smithy: improved {improved}, skipped {skipped}, {pending.Count} pending. queue_wait_seconds={waitSec}";
            }

            // Nothing clickable and nothing waiting for the remaining targets — avoid an infinite loop.
            Notify($"Smithy: no actionable state for {pending.Count} pending troop(s); stopping. Improved {improved}, skipped {skipped}.");
            break;
        }

        await GotoAsync(Paths.Buildings, cancellationToken);

        // All selected troops resolved to a terminal state (at target / maxed / smithy level too low /
        // not researched) and nothing was improved this run: report "All done" so the task runner
        // permanently blocks the Troops group (ThrowIfTroopsGroupBlocked -> troops_blocked=all_done). The
        // desktop then switches the dashboard "Upgrade Troops" card OFF instead of re-running every loop.
        var nothingToDo = pending.Count == 0 && improved == 0;
        return nothingToDo
            ? $"Smithy: All done — nothing left to upgrade (improved {improved}, skipped {skipped})."
            : $"Smithy: improved {improved} troop(s), skipped {skipped}.";
    }

    // Emits one raw object per Smithy troop row for the browser-free SmithyPageParser to classify.
    private async Task<string> ReadSmithyRowsJsonAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const clean = (v) => String(v || '').replace(/\s+/g, ' ').trim();
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  const out = [];
                  for (const row of rows) {
                    const img = row.querySelector('img.unit');
                    const unitClass = img ? String(img.className || '') : '';

                    let name = '';
                    for (const a of Array.from(row.querySelectorAll('.title a'))) {
                      const t = clean(a.textContent);
                      if (t) { name = t; break; }
                    }
                    if (!name && img) { name = clean(img.getAttribute('alt')); }

                    const levelText = clean(row.querySelector('.title .level')?.textContent
                      || row.querySelector('.level')?.textContent || '');
                    const errorText = clean(row.querySelector('.errorMessage')?.textContent || '');
                    const fullyDeveloped = /fully\s+(developed|researched)/i.test(clean(row.textContent));

                    // Hidden countdown inside the resource-shortage message = seconds until enough resources.
                    let errorWaitSeconds = null;
                    const errTimer = row.querySelector('.errorMessage .timer');
                    const errTimerVal = errTimer ? String(errTimer.getAttribute('value') || '') : '';
                    if (/^\d+$/.test(errTimerVal)) { errorWaitSeconds = parseInt(errTimerVal, 10); }

                    const candidates = Array.from(row.querySelectorAll('button, input[type="submit"], input[type="button"], a'));
                    let researchOnClick = '';
                    let researchValue = '';
                    let hasResearchButton = false;
                    let primaryValue = '';
                    for (const b of candidates) {
                      const oc = String(b.getAttribute('onclick') || '') + ' ' + String(b.getAttribute('href') || '');
                      const val = clean(String(b.getAttribute('value') || '') + ' ' + String(b.textContent || '')).toLowerCase();
                      const cls = String(b.className || '').toLowerCase();
                      if (!val && !oc.trim()) continue;
                      if (/\d+\s*%|faster|video/i.test(val)) continue;           // speed-up button
                      if (!primaryValue && val) primaryValue = val;             // first real button (may be "exchange resources")
                      if (cls.includes('gold') || /exchange|npc|instant|open shop/i.test(val)) continue;
                      const isResearch = /action=research/i.test(oc) || /^(improve|upgrade)\b/.test(val);
                      if (!isResearch) continue;
                      const disabled = b.disabled === true || cls.includes('disabled') || b.getAttribute('aria-disabled') === 'true';
                      researchOnClick = oc;
                      researchValue = clean(b.getAttribute('value') || b.textContent || '');
                      hasResearchButton = !disabled;
                      if (!disabled) break;
                    }

                    out.push({
                      name,
                      unitClass,
                      buttonOnClick: researchOnClick,
                      levelText,
                      buttonValue: researchValue || primaryValue,
                      errorText,
                      errorWaitSeconds,
                      hasResearchButton,
                      fullyDeveloped
                    });
                  }
                  return JSON.stringify(out);
                }
                """);
        }
        catch
        {
            return "[]";
        }
    }

    // Clicks the Improve/Upgrade button for the row identified by key ("u21" unit id, or "t1" troop slot).
    private async Task<bool> ClickSmithyImproveButtonForKeyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            return await _page.EvaluateAsync<bool>(
                """
                (key) => {
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  const wantUnit = key && key[0] === 'u' ? parseInt(key.slice(1), 10) : null;
                  const wantT = key && key[0] === 't' ? key : null;
                  for (const row of rows) {
                    const img = row.querySelector('img.unit');
                    const um = /\bu(\d+)\b/.exec(img ? String(img.className || '') : '');
                    const unit = um ? parseInt(um[1], 10) : null;

                    let btn = null;
                    for (const b of Array.from(row.querySelectorAll('button, input[type="submit"], input[type="button"], a'))) {
                      const oc = String(b.getAttribute('onclick') || '') + ' ' + String(b.getAttribute('href') || '');
                      const val = (String(b.getAttribute('value') || '') + ' ' + String(b.textContent || '')).replace(/\s+/g, ' ').trim().toLowerCase();
                      const cls = String(b.className || '').toLowerCase();
                      if (/\d+\s*%|faster|video/i.test(val)) continue;
                      if (cls.includes('gold') || /exchange|npc|instant|open shop/i.test(val)) continue;
                      const disabled = b.disabled === true || cls.includes('disabled') || b.getAttribute('aria-disabled') === 'true';
                      if (disabled) continue;
                      const isResearch = /action=research/i.test(oc) || /^(improve|upgrade)\b/.test(val);
                      if (!isResearch) continue;
                      btn = b;
                      break;
                    }
                    if (!btn) continue;

                    const oc = String(btn.getAttribute('onclick') || '') + ' ' + String(btn.getAttribute('href') || '');
                    const tm = /[?&]t=(t\d+)\b/.exec(oc);
                    const tkey = tm ? tm[1] : null;
                    const match = (wantUnit !== null && unit === wantUnit) || (wantT !== null && tkey === wantT);
                    if (!match) continue;
                    btn.click();
                    return true;
                  }
                  return false;
                }
                """,
                key);
        }
        catch
        {
            return false;
        }
    }

    // Reads active Smithy research strictly from the queue table, never from troop-row duration labels.
    private async Task<IReadOnlyList<SmithyQueueEntry>> ReadSmithyQueueEntriesAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            var rawJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
                  const roots = Array.from(document.querySelectorAll('table.under_progress, .under_progress'));
                  const rows = [];
                  const seenTimers = new Set();
                  for (const root of roots) {
                    const timers = Array.from(root.querySelectorAll('.timer, [id^="timer"]'));
                    for (const timer of timers) {
                      if (seenTimers.has(timer)) continue;
                      seenTimers.add(timer);
                      const row = timer.closest('tr, li, .queueRow, .research') || timer.parentElement;
                      if (!row) continue;
                      const image = row.querySelector('img.unit, img[class*="u"]');
                      const nameNode = row.querySelector('.name, .unitName, .researchName, .title');
                      rows.push({
                        timerValue: clean(timer.getAttribute('value')),
                        timerText: clean(timer.textContent),
                        name: clean(nameNode && nameNode.textContent),
                        imageAlt: clean(image && (image.getAttribute('alt') || image.getAttribute('title'))),
                        rowText: clean(row.innerText || row.textContent)
                      });
                    }
                  }
                  return JSON.stringify(rows);
                }
                """);
            return SmithyPageParser.ParseQueueEntries(rawJson);
        }
        catch (Exception ex)
        {
            Notify($"[smithy-queue] read failed: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<int>> ReadSmithyQueueTimersAsync(CancellationToken cancellationToken)
    {
        return (await ReadSmithyQueueEntriesAsync(cancellationToken))
            .Select(entry => entry.RemainingSeconds)
            .ToList();
    }

    // Extracts a "queue_wait_seconds=N" hint from a result string (e.g. the Smithy building upgrade result
    // when the construction queue is full), so the troop-upgrade task can defer on that exact timer.
    private static bool TryReadQueueWaitSeconds(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        const string token = "queue_wait_seconds=";
        var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + token.Length;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return end > start
            && int.TryParse(text.AsSpan(start, end - start), out seconds)
            && seconds > 0;
    }

    // Tops the targeted troop up from the hero inventory by opening that troop row's resource-transfer
    // dialog and confirming it — but only when Travian enables "Transfer selected" (i.e. the hero can
    // actually cover the cost), so an ineffective partial transfer never spends the hero's resources.
    // Official-only and opt-in (gated by the caller). Best-effort: any failure returns false and the
    // caller falls back to waiting for the village to accumulate resources.
    private async Task<bool> TryHeroResourceTransferForSmithyTroopAsync(string key, string label, CancellationToken cancellationToken)
    {
        bool opened;
        try
        {
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            opened = await _page.EvaluateAsync<bool>(
                """
                (key) => {
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  const wantUnit = key && key[0] === 'u' ? parseInt(key.slice(1), 10) : null;
                  const wantT = key && key[0] === 't' ? key : null;
                  for (const row of rows) {
                    const img = row.querySelector('img.unit');
                    const um = /\bu(\d+)\b/.exec(img ? String(img.className || '') : '');
                    const unit = um ? parseInt(um[1], 10) : null;
                    let tkey = null;
                    const onclicks = Array.from(row.querySelectorAll('[onclick]'))
                      .map(e => String(e.getAttribute('onclick') || '')).join(' ');
                    const tm = /[?&]t=(t\d+)\b/.exec(onclicks);
                    if (tm) tkey = tm[1];
                    const match = (wantUnit !== null && unit === wantUnit) || (wantT !== null && tkey === wantT);
                    if (!match) continue;
                    const icon = row.querySelector('.inlineIcon.resource.transfer');
                    if (!icon) return false;
                    icon.click();
                    return true;
                  }
                  return false;
                }
                """,
                key);
        }
        catch
        {
            return false;
        }

        if (!opened)
        {
            Notify($"[hero-transfer] smithy: no hero transfer offered for '{label}'.");
            return false;
        }

        try
        {
            await _page.WaitForSelectorAsync(
                "div.resourceTransferDialog, #dialogContent",
                new PageWaitForSelectorOptions { Timeout = 8000 });
        }
        catch
        {
            Notify($"[hero-transfer] smithy: transfer dialog did not appear for '{label}'.");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        bool confirmed;
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent') : null);
                  if (!dialog) return false;
                  let button = dialog.querySelector('.actionButton.preSelected button');
                  if (!button) {
                    button = Array.from(dialog.querySelectorAll('button')).find(b => /transfer selected/i.test(b.textContent || ''));
                  }
                  return !!button && !button.disabled && button.getAttribute('aria-disabled') !== 'true';
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            confirmed = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const dialog = document.querySelector('div.resourceTransferDialog')
                    || ((document.querySelector('#dialogContent h3')?.textContent || '').trim().toLowerCase() === 'transfer resources'
                      ? document.querySelector('#dialogContent') : null);
                  if (!dialog) return false;
                  let button = dialog.querySelector('.actionButton.preSelected button');
                  if (!button) {
                    button = Array.from(dialog.querySelectorAll('button')).find(b => /transfer selected/i.test(b.textContent || ''));
                  }
                  if (!button) return false;
                  button.click();
                  return true;
                }
                """);
        }
        catch (TimeoutException)
        {
            Notify($"[hero-transfer] smithy: 'Transfer selected' stayed disabled for '{label}' (hero can't cover). Closing.");
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }
        catch
        {
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        if (!confirmed)
        {
            await TryDismissResourceTransferDialogAsync(cancellationToken);
            return false;
        }

        Notify($"[hero-transfer] smithy: transferred hero resources for '{label}'.");
        await WaitForPageReadyAsync(cancellationToken);
        await TryDismissResourceTransferDialogAsync(cancellationToken);
        return true;
    }

    private async Task TryReloadSmithyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            // Transient navigation race during reload. The next iteration's IsCurrentUrlForPath
            // check + GotoAsync will recover by re-navigating to the smithy page.
            Notify($"Smithy: reload hit transient navigation context ({ex.Message}). Continuing.");
            await Task.Delay(300, cancellationToken);
        }
    }

    public async Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default)
    {
        Notify("ReadSmithyUpgradeStatusAsync started");

        var smithySlotId = ResolveKnownSmithySlotId(knownBuildings) ?? await TryResolveSmithySlotIdAsync(cancellationToken);
        if (!smithySlotId.HasValue)
        {
            return new SmithyUpgradeStatus(
                SmithyExists: false,
                SmithySlotId: null,
                ActiveUpgradeCount: 0,
                RemainingSeconds: null,
                ActiveUpgradeRemainingSeconds: [],
                RemainingText: "N/A",
                StatusText: "Smithy not found.");
        }

        await GotoAsync(Paths.BuildBySlot(smithySlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the Smithy queue.", cancellationToken);
        await EnsureLoggedInAsync();

        var activeQueue = (await ReadSmithyQueueEntriesAsync(cancellationToken))
            .OrderBy(entry => entry.RemainingSeconds)
            .ToList();
        var activeTimers = activeQueue.Select(entry => entry.RemainingSeconds).ToList();
        var remainingSeconds = activeTimers.Count > 0 ? activeTimers[0] : (int?)null;
        var activeUpgrades = activeQueue
            .Select(entry => new ActiveSmithyUpgrade(
                entry.Name,
                entry.TargetLevel,
                entry.RemainingSeconds,
                TimerSnapshot.FromRemaining(entry.RemainingSeconds)))
            .ToList();

        return new SmithyUpgradeStatus(
            SmithyExists: true,
            SmithySlotId: smithySlotId.Value,
            ActiveUpgradeCount: activeTimers.Count,
            RemainingSeconds: remainingSeconds,
            ActiveUpgradeRemainingSeconds: activeTimers,
            RemainingText: remainingSeconds is > 0 ? TravianParsing.FormatDuration(remainingSeconds.Value) : "Ready",
            StatusText: activeTimers.Count > 0
                ? $"Smithy upgrade{(activeTimers.Count == 1 ? string.Empty : "s")} active."
                : "Ready.",
            ActiveUpgradeFinishes: activeUpgrades.Select(entry => entry.Finish!).ToList(),
            ActiveUpgrades: activeUpgrades);
    }

    public async Task<string> ReadSmithyQueueFromCurrentPageTestAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before the Smithy queue test.", cancellationToken);

        if (!await IsCurrentPageSmithyAsync(cancellationToken))
        {
            return "Smithy queue test: current page does not look like the Smithy page.";
        }

        var activeQueue = (await ReadSmithyQueueEntriesAsync(cancellationToken))
            .OrderBy(entry => entry.RemainingSeconds)
            .ToList();
        if (activeQueue.Count <= 0)
        {
            return "Smithy queue test: ready. No active Smithy upgrade found on the current page.";
        }

        var entriesText = string.Join(
            ", ",
            activeQueue.Select(entry =>
                $"{entry.Name} -> {(entry.TargetLevel.HasValue ? $"level {entry.TargetLevel.Value}" : "next level")} ({TravianParsing.FormatDuration(entry.RemainingSeconds)})"));
        return $"Smithy queue test: active={activeQueue.Count}, entries=[{entriesText}]";
    }

    private static int? ResolveKnownSmithySlotId(IReadOnlyList<Building>? knownBuildings)
    {
        return knownBuildings?
            .FirstOrDefault(item =>
                item.SlotId is > 0
                && (item.Gid == 13 || string.Equals(item.Name, "Smithy", StringComparison.OrdinalIgnoreCase)))
            ?.SlotId;
    }

    private async Task<bool> IsCurrentPageSmithyAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const headingNodes = Array.from(document.querySelectorAll('h1, h2, h3, .titleInHeader, .build_details .title, .researches .title'));
                  for (const node of headingNodes) {
                    const text = clean(node.textContent);
                    if (text.includes('smithy') || text.includes('blacksmith')) {
                      return true;
                    }
                  }

                  if (document.querySelector('.build_details.researches .research')) {
                    return true;
                  }

                  const bodyText = clean(document.body && document.body.innerText);
                  return bodyText.includes('improve the blacksmith')
                    || bodyText.includes('improve the smithy')
                    || bodyText.includes('fully researched')
                    || bodyText.includes('fully developed');
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private async Task<int?> TryResolveSmithySlotIdAsync(CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);

            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the building overview.", cancellationToken);
            await EnsureLoggedInAsync();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while resolving the Smithy slot.", cancellationToken);

            var slots = await ReadBuildingInfosAsync(cancellationToken);
            // Smithy is gid 13 (see ENGINEERING_NOTES §5: no separate Armoury on gid 12). Accept 12 as a
            // defensive fallback so a mislabeled overview entry still resolves the slot.
            var smithyEntry = slots.FirstOrDefault(kvp =>
                ParseGidFromBuildingCode(kvp.Value.BuildingCode) is 13 or 12 && kvp.Value.Level > 0);
            if (smithyEntry.Value is not null)
            {
                Notify($"Smithy found at slot {smithyEntry.Key} on overview attempt {attempt}/{attempts}.");
                return smithyEntry.Key;
            }

            if (attempt < attempts)
            {
                Notify($"Smithy not detected on overview attempt {attempt}/{attempts}. Reloading and retrying.");
                await Task.Delay(350, cancellationToken);
            }
        }

        return null;
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

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
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

    // Reads the remaining seconds of an in-progress demolition (or any build queue timer)
    // from the Main Building page. Travian countdown timers carry a `value` attribute with
    // the seconds remaining, which is far more reliable than parsing the displayed text.
    private async Task<int?> ReadActiveDemolitionSecondsOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const seconds = [];
              const pushTimer = (node) => {
                if (!node) return;
                const attr = node.getAttribute && node.getAttribute('value');
                const n = attr != null ? Number(attr) : NaN;
                if (Number.isFinite(n) && n > 0) seconds.push(n);
              };
              const containers = document.querySelectorAll(
                '.buildingList, #building_contract, .underConstruction, .demolish, #demolish, .boxes-contents, .content');
              for (const c of containers) {
                for (const t of c.querySelectorAll('.timer, [id^="timer"], [counting="down"]')) {
                  pushTimer(t);
                }
              }
              return JSON.stringify(seconds);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<int>()
            : JsonSerializer.Deserialize<List<int>>(rawJson) ?? new List<int>();
        if (raw.Count == 0)
        {
            return null;
        }

        // The longest timer represents the active demolition/construction we must outlast.
        return raw.Max();
    }

    // Polls the Main Building page until no demolition/build countdown remains.
    // Returns true if a demolition was actually in progress and we waited for it.
    private async Task<bool> WaitForActiveDemolitionToFinishAsync(string mainBuildingPath, CancellationToken cancellationToken)
    {
        const int maxTotalWaitSeconds = 20 * 60; // safety cap
        const int maxChunkSeconds = 30;
        var waitedSeconds = 0;
        var waitedAtLeastOnce = false;

        while (waitedSeconds < maxTotalWaitSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = await ReadActiveDemolitionSecondsOnCurrentPageAsync(cancellationToken);
            if (remaining is not > 0)
            {
                return waitedAtLeastOnce;
            }

            waitedAtLeastOnce = true;
            var chunk = Math.Min(remaining.Value, maxChunkSeconds);
            Notify($"Demolition/build in progress (~{remaining.Value}s remaining). Waiting {chunk}s.");
            await Task.Delay(chunk * 1000 + 500, cancellationToken);
            waitedSeconds += chunk + 1;

            await ReloadOrGotoAsync(mainBuildingPath, cancellationToken);
        }

        Notify("Stopped waiting for demolition: safety cap reached.");
        return waitedAtLeastOnce;
    }

    private async Task<IReadOnlyList<Building>> ReadBuildingsAsync(CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.Buildings, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the building overview.", cancellationToken);

        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading buildings.", cancellationToken);

        Dictionary<int, BuildingInfo> buildingsBySlot = new();
        await RetryAsync("read building slots snapshot", async () =>
        {
            buildingsBySlot = await ReadBuildingInfosAsync(cancellationToken);
        }, cancellationToken: cancellationToken);

        return buildingsBySlot.Values
            .OrderBy(item => item.SlotId)
            .Select(item => new Building(
                item.SlotId,
                item.BuildingName,
                item.LevelKnown || !item.HasOccupancyEvidence ? item.Level : null,
                ResolveUrl(Paths.BuildBySlot(item.SlotId)),
                ParseGidFromBuildingCode(item.BuildingCode)))
            .ToList();
    }

    private async Task<Dictionary<int, BuildingInfo>> ReadBuildingInfosAsync(CancellationToken cancellationToken)
    {
        var firstScan = await ScanBuildingOverviewAsync(cancellationToken);
        if (!ShouldRetryBuildingOverviewScan(firstScan))
        {
            return firstScan.Buildings;
        }

        Notify($"Building overview scan looked incomplete ({DescribeBuildingOverviewScan(firstScan)}). Reloading once.");

        await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared while retrying the building overview scan.", cancellationToken);
        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while waiting for the building overview retry.", cancellationToken);

        var secondScan = await ScanBuildingOverviewAsync(cancellationToken);
        return ChoosePreferredBuildingOverviewScan(firstScan, secondScan).Buildings;
    }

    private async Task<BuildingOverviewScanResult> ScanBuildingOverviewAsync(CancellationToken cancellationToken)
    {
        await WaitForBuildingOverviewReadyAsync(cancellationToken);
        var slots = await ReadBuildingOverviewSlotSnapshotsAsync(cancellationToken);
        return ParseBuildingOverviewScan(slots);
    }

    private async Task WaitForBuildingOverviewReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const slots = Array.from(document.querySelectorAll('div.buildingSlot'));
                  if (slots.length < 18) {
                    return false;
                  }

                  // Wait until building images have populated their gid classes.
                  // V3 layouts add `g<gid>` to the slot/img after async hydration; if we read
                  // too early, occupied slots look empty (missing_gid spam in logs).
                  const slotsWithGid = slots.filter(slot => {
                    const slotClass = String(slot.className || '');
                    if (/\bg\d{1,2}\b/i.test(slotClass)) return true;
                    const img = slot.querySelector('img.building, img[class*=" g"], img[class^="g"]');
                    return img && /\bg\d{1,2}\b/i.test(String(img.className || ''));
                  }).length;

                  // Typical T4 villages have 18+ slots with at least ~10 occupied buildings
                  // by the time the player builds anything; require a reasonable share to
                  // confirm the page has hydrated.
                  return slotsWithGid >= Math.min(slots.length, 12);
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 4000 });
        }
        catch (TimeoutException)
        {
            // Continue with the best available DOM snapshot.
        }
        catch (Exception ex) when (!IsTransientExecutionContextException(ex))
        {
            Notify($"Building overview ready wait skipped: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<BuildingOverviewSlotSnapshot>> ReadBuildingOverviewSlotSnapshotsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
              const slots = Array.from(document.querySelectorAll('div.buildingSlot'));
              return JSON.stringify(slots.map((slot, index) => {
                const label = slot.querySelector('.labelLayer, .level, .label');
                const namedElement = slot.querySelector('.name, .title, .desc, .buildingName, .hover, [title], [aria-label], img[alt]');
                const link = slot.querySelector('a[href], area[href]');
                const image = slot.querySelector('img[alt]');
                const dataName = clean(slot.getAttribute('data-name') || '');
                const dataLevel = clean(slot.getAttribute('data-level') || link?.getAttribute('data-level') || '');
                const buildingImg = slot.querySelector('img.building, img[class*=" g"], img[class^="g"]');
                const buildingImgClass = buildingImg ? String(buildingImg.className || '') : '';
                const text = clean(slot.textContent || '');
                const slotOwnClass = String(slot.className || '');
                // V3 layouts sometimes leave the gid class only on the inner <img>.
                // Merge both so the C# parser sees `g<gid>` regardless of which element carries it.
                const className = (slotOwnClass + ' ' + buildingImgClass).trim();
                const occupiedEvidence =
                  /\bg\d{1,2}\b/i.test(className)
                  || /underconst|underconstruction|built|occupied/i.test(className)
                  || Boolean(link)
                  || /\blevel\s*\d+\b/i.test(text);

                return {
                  index,
                  className,
                  outerHtml: slot.outerHTML || '',
                  levelText: clean(label ? label.textContent : ''),
                  dataLevelText: dataLevel,
                  dataNameText: dataName,
                  nameText: clean(namedElement ? namedElement.textContent : ''),
                  titleText: clean(slot.getAttribute('title') || (namedElement ? namedElement.getAttribute('title') : '') || (link ? link.getAttribute('title') : '') || ''),
                  altText: clean(image ? image.getAttribute('alt') : ''),
                  text,
                  occupiedEvidence
                };
              }));
            }
            """);

        return JsonSerializer.Deserialize<List<BuildingOverviewSlotSnapshot>>(
            rawJson ?? "[]",
            BuildingOverviewSnapshotJsonOptions) ?? [];
    }

    private static BuildingOverviewScanResult ParseBuildingOverviewScan(IReadOnlyList<BuildingOverviewSlotSnapshot> slotSnapshots)
    {
        var buildings = new Dictionary<int, BuildingInfo>();

        foreach (var slotSnapshot in slotSnapshots)
        {
            try
            {
                var info = CreateBuildingInfo(slotSnapshot);
                if (info is null)
                {
                    continue;
                }

                buildings[info.SlotId] = info;
            }
            catch
            {
                // Ignore malformed individual slots and keep parsing the rest of the overview.
            }
        }

        var occupiedSlots = buildings.Values
            .Where(item => item.HasOccupancyEvidence)
            .ToList();
        var missingBuildingCodeCount = occupiedSlots.Count(item => ParseGidFromBuildingCode(item.BuildingCode) is null);
        var unknownLevelCount = occupiedSlots.Count(item => !item.LevelKnown);
        var hasMainBuilding = ContainsBuilding(buildings.Values, 15, "Main Building");
        var hasRallyPoint = ContainsBuilding(buildings.Values, 16, "Rally Point");

        var confidence = BuildingOverviewScanConfidence.High;
        if (buildings.Count < 18
            || !hasMainBuilding
            || missingBuildingCodeCount >= 3
            || unknownLevelCount >= 3)
        {
            confidence = BuildingOverviewScanConfidence.Low;
        }
        else if (buildings.Count < 22
            || missingBuildingCodeCount > 0
            || unknownLevelCount > 0
            || !hasRallyPoint)
        {
            confidence = BuildingOverviewScanConfidence.Medium;
        }

        return new BuildingOverviewScanResult
        {
            Buildings = buildings,
            Confidence = confidence,
            MissingBuildingCodeCount = missingBuildingCodeCount,
            UnknownLevelCount = unknownLevelCount,
            MissingMainBuilding = !hasMainBuilding,
            MissingRallyPoint = !hasRallyPoint,
        };
    }

    private static BuildingInfo? CreateBuildingInfo(BuildingOverviewSlotSnapshot slotSnapshot)
    {
        var classes = (slotSnapshot.ClassName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var slotId = TryExtractSlotId(classes)
            ?? TryExtractSlotIdFromText(slotSnapshot.OuterHtml);
        if (slotId is null || slotId < 19 || slotId > 40)
        {
            return null;
        }

        var buildingCode = TryExtractBuildingCode(classes)
            ?? TryExtractBuildingCodeFromText(slotSnapshot.OuterHtml);
        var level = TryParseOverviewLevel(slotSnapshot.LevelText, slotSnapshot.DataLevelText, slotSnapshot.Text);
        var levelKnown = level.HasValue;
        var nameCandidate = SelectBuildingNameCandidate(slotSnapshot.DataNameText, slotSnapshot.NameText, slotSnapshot.TitleText, slotSnapshot.AltText);
        var hasOccupancyEvidence = slotSnapshot.OccupiedEvidence
            || !string.IsNullOrWhiteSpace(buildingCode)
            || !string.IsNullOrWhiteSpace(nameCandidate);

        buildingCode ??= TryResolveBuildingCodeFromName(nameCandidate);

        var gid = ParseGidFromBuildingCode(buildingCode);
        var normalizedLevel = level ?? 0;

        if (slotId.Value == 40 && gid is 31 or 32 or 33 or 42 or 43 && normalizedLevel == 0)
        {
            normalizedLevel = 1;
            levelKnown = true;
        }

        if (string.IsNullOrEmpty(buildingCode) && normalizedLevel > 0 && slotId.Value == 39)
        {
            buildingCode = "g16";
        }

        var buildingName = ResolveBuildingDisplayName(
            buildingCode,
            nameCandidate,
            hasOccupancyEvidence);

        return new BuildingInfo
        {
            SlotId = slotId.Value,
            BuildingCode = buildingCode ?? string.Empty,
            BuildingName = buildingName,
            Level = normalizedLevel,
            LevelKnown = levelKnown,
            HasOccupancyEvidence = hasOccupancyEvidence,
        };
    }

    private static BuildingOverviewScanResult ChoosePreferredBuildingOverviewScan(
        BuildingOverviewScanResult firstScan,
        BuildingOverviewScanResult secondScan)
    {
        return GetBuildingOverviewScanScore(secondScan) >= GetBuildingOverviewScanScore(firstScan)
            ? secondScan
            : firstScan;
    }

    private static int GetBuildingOverviewScanScore(BuildingOverviewScanResult scan)
    {
        var baseScore = scan.Confidence switch
        {
            BuildingOverviewScanConfidence.High => 300,
            BuildingOverviewScanConfidence.Medium => 200,
            _ => 100,
        };

        return baseScore
            + (scan.Buildings.Count * 5)
            - (scan.MissingBuildingCodeCount * 20)
            - (scan.UnknownLevelCount * 20)
            - (scan.MissingMainBuilding ? 60 : 0)
            - (scan.MissingRallyPoint ? 20 : 0);
    }

    private static bool ShouldRetryBuildingOverviewScan(BuildingOverviewScanResult scan)
    {
        // Only retry when the scan is *useless* (no buildings parsed, or Main Building missing).
        // Downstream callers like UpgradeBuildingToMaxAsync only need ONE specific slot — they
        // don't care if 19 other occupied slots failed to hydrate gids. The previous rule
        // re-loaded dorf2 every iteration on fast/young villages where many slots legitimately
        // lacked gids in the first scan, wasting ~1.5s per upgrade attempt.
        if (scan.Buildings.Count < 18)
        {
            return true;
        }

        if (scan.MissingMainBuilding)
        {
            return true;
        }

        return false;
    }

    private static string DescribeBuildingOverviewScan(BuildingOverviewScanResult scan)
    {
        return $"slots={scan.Buildings.Count}, missing_gid={scan.MissingBuildingCodeCount}, unknown_level={scan.UnknownLevelCount}, main={(scan.MissingMainBuilding ? "missing" : "ok")}, rally={(scan.MissingRallyPoint ? "missing" : "ok")}";
    }

    private static bool ContainsBuilding(IEnumerable<BuildingInfo> buildings, int gid, string name)
    {
        return buildings.Any(item =>
            ParseGidFromBuildingCode(item.BuildingCode) == gid
            || BuildingNames.Same(item.BuildingName, name));
    }

    private static int? TryParseOverviewLevel(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryParsePositiveInt(candidate, out var parsedLevel))
            {
                return parsedLevel;
            }

            var fallbackMatch = OverviewLevelRegex.Match(candidate ?? string.Empty);
            if (fallbackMatch.Success
                && int.TryParse(fallbackMatch.Groups["level"].Value, out parsedLevel))
            {
                return parsedLevel;
            }
        }

        return null;
    }

    private static bool TryParsePositiveInt(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return int.TryParse(text.Trim(), out value) && value >= 0;
    }

    private static int? TryExtractSlotIdFromText(string? text)
    {
        return TryExtractIntFromRegex(AidClassRegex, text, "id")
            ?? TryExtractIntFromRegex(FallbackSlotClassRegex, text, "id")
            ?? TryExtractIntFromRegex(BuildingSlotQueryRegex, text, "id")
            ?? TryExtractIntFromRegex(BuildingSlotDataRegex, text, "id");
    }

    private static string? TryExtractBuildingCodeFromText(string? text)
    {
        var gid = TryExtractIntFromRegex(BuildingCodeClassRegex, text, "gid")
            ?? TryExtractIntFromRegex(BuildingGidQueryRegex, text, "gid")
            ?? TryExtractIntFromRegex(BuildingGidDataRegex, text, "gid");
        return gid is > 0 ? $"g{gid.Value}" : null;
    }

    private static int? TryExtractIntFromRegex(Regex regex, string? text, string groupName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[groupName].Value, out var value)
            ? value
            : null;
    }

    private static string? TryResolveBuildingCodeFromName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = BuildingNames.Normalize(candidate ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (NormalizedBuildingCodesByName.Value.TryGetValue(normalized, out var buildingCode))
            {
                return buildingCode;
            }
        }

        return null;
    }

    private static string ResolveBuildingDisplayName(string? buildingCode, string? nameCandidate, bool hasOccupancyEvidence)
    {
        if (!string.IsNullOrWhiteSpace(buildingCode)
            && !string.Equals(buildingCode, "g0", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveBuildingName(buildingCode);
        }

        if (!string.IsNullOrWhiteSpace(nameCandidate))
        {
            return nameCandidate!;
        }

        return hasOccupancyEvidence ? "Unknown" : "Empty";
    }

    private static string? SelectBuildingNameCandidate(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var sanitized = SanitizeBuildingNameCandidate(candidate);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                return sanitized;
            }
        }

        return null;
    }

    private static string? SanitizeBuildingNameCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var cleaned = string.Join(" ", candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (int.TryParse(cleaned, out _))
        {
            return null;
        }

        var lowered = cleaned.ToLowerInvariant();
        if (lowered.Contains("building site", StringComparison.Ordinal)
            || lowered.Contains("empty site", StringComparison.Ordinal)
            || lowered.Contains("construct", StringComparison.Ordinal)
            || lowered.Contains("free site", StringComparison.Ordinal)
            || lowered.Contains("click to build", StringComparison.Ordinal))
        {
            return null;
        }

        return cleaned;
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
        if (string.IsNullOrWhiteSpace(buildingCode)
            || string.Equals(buildingCode, "g0", StringComparison.OrdinalIgnoreCase))
        {
            return "Empty";
        }

        return TravianBuildings.TryGetValue(buildingCode, out var buildingName)
            ? buildingName
            : buildingCode;
    }

    private static int? ParseGidFromBuildingCode(string? buildingCode)
    {
        if (string.IsNullOrWhiteSpace(buildingCode)
            || string.Equals(buildingCode, "g0", StringComparison.OrdinalIgnoreCase)
            || buildingCode.Length < 2)
        {
            return null;
        }

        return int.TryParse(buildingCode[1..], out var gid)
            ? gid
            : null;
    }

    private async Task<Building?> WaitForDemolitionLevelChangeAsync(
        int slotId,
        int previousLevel,
        CancellationToken cancellationToken)
    {
        var statusAfterStart = await ReadVillageStatusAsync(cancellationToken);
        var queueWaitSeconds = Math.Max(20, (statusAfterStart.BuildQueueRemainingSeconds ?? 0) + 30);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(queueWaitSeconds);

        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            var currentStatus = await ReadVillageStatusAsync(cancellationToken);
            var current = currentStatus.Buildings.FirstOrDefault(item => item.SlotId == slotId);
            var currentLevel = current?.Level ?? 0;
            if (current is null || currentLevel < previousLevel || !IsOccupiedBuilding(current))
            {
                return current;
            }
        }

        return statusAfterStart.Buildings.FirstOrDefault(item => item.SlotId == slotId);
    }

    private static bool IsOccupiedBuilding(Building? building)
    {
        if (building is null)
        {
            return false;
        }

        if ((building.Gid ?? 0) <= 0
            && ((building.Level ?? 0) <= 0
                || string.IsNullOrWhiteSpace(building.Name)
                || string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<BuildQueueItem>> ReadBuildQueueAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the build queue.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const parseNumber = (value) => {
                if (value == null || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const readUrlParam = (href, names) => {
                if (!href) return null;
                try {
                  const url = new URL(href, window.location.href);
                  for (const name of names) {
                    const parsed = parseNumber(url.searchParams.get(name));
                    if (parsed != null) return parsed;
                  }
                } catch {
                  for (const name of names) {
                    const match = href.match(new RegExp(`[?&]${name}=(\\d{1,2})(?:\\D|$)`, 'i'));
                    if (match) return Number(match[1]);
                  }
                }
                return null;
              };
              const readElementNumber = (element, attrs, regexes) => {
                for (const attr of attrs) {
                  const parsed = parseNumber(element.getAttribute(attr));
                  if (parsed != null) return parsed;
                }
                const classText = String(element.className || '');
                for (const regex of regexes) {
                  const match = classText.match(regex);
                  if (match) return Number(match[1]);
                }
                return null;
              };
              const selectors = [
                '.buildingList li',
                '#building_contract li',
                '.underConstruction',
                '.buildDuration',
                'table.buildingList tr'
              ];

              // Each matched element is one active construction. Count them per element (NOT deduped by
              // text): two simultaneous upgrades of the same building/field have identical text and must
              // still count as two. Return the first selector that yields any entries.
              for (const selector of selectors) {
                const items = [];
                for (const element of document.querySelectorAll(selector)) {
                  const text = (element.textContent || '').replace(/\s+/g, ' ').trim();
                  if (!text) continue;
                  const timeElement = element.querySelector('.timer, .countdown, .value, [counting="down"], [id^="timer"]');
                  const nameElement = element.querySelector('.name');
                  const durationElement = element.querySelector('.buildDuration');
                  // Broad fallback selectors such as ".buildingList li" can also match nested action/
                  // detail list items. Only count rows that carry actual construction identity/timing.
                  if (!timeElement && !nameElement && !durationElement) continue;
                  const timeLeft = timeElement ? (timeElement.textContent || '').trim() : null;
                  const link = element.querySelector('a[href*="build.php"], a[href*="dorf1.php"], a[href*="dorf2.php"]');
                  const href = link ? (link.getAttribute('href') || '') : '';
                  const slotId =
                    readUrlParam(href, ['id', 'a'])
                    ?? readElementNumber(element, ['data-aid', 'data-slot', 'data-slot-id', 'data-building-slot-id', 'data-id'], [/\baid(\d{1,2})\b/i, /\ba(\d{1,2})\b/i]);
                  const gid =
                    readUrlParam(href, ['gid'])
                    ?? readElementNumber(element, ['data-gid', 'data-building-gid', 'data-type'], [/\bg(\d{1,2})\b/i]);
                  items.push({ text, timeLeft, slotId, gid, href: href || null });
                }
                if (items.length) return JSON.stringify(items);
              }
              return JSON.stringify([]);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<BuildQueueJs>()
            : JsonSerializer.Deserialize<List<BuildQueueJs>>(rawJson) ?? new List<BuildQueueJs>();

        raw ??= [];
        return raw
            .Where(i => !string.IsNullOrWhiteSpace(i.Text))
            .Select(i => new BuildQueueItem(i.Text!, i.TimeLeft, i.SlotId, i.Gid, i.Href))
            .ToList();
    }


    public async Task<IReadOnlyList<ActiveConstruction>> ReadActiveConstructionsAsync(
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true)
    {
        // Cache hit collapses the 4-5 calls a single upgrade iteration makes (CheckQueueOrDefer,
        // ReadHighestKnownQueuedBuildingLevel, ReadQueuedBuildingWaitSeconds, level-advance poll)
        // into one network round-trip. GotoAsync invalidates the cache automatically.
        if (_cachedActiveConstructions is not null
            && DateTimeOffset.UtcNow - _cachedActiveConstructionsAt < ActiveConstructionsCacheTtl)
        {
            _lastActiveConstructionsFromOverview = _cachedActiveConstructionsFromOverview;
            return _cachedActiveConstructions;
        }

        LogFunctionStarted();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading active constructions.", cancellationToken);
        _lastActiveConstructionsFromOverview = false;

        var raw = await ReadActiveConstructionsOnCurrentPageAsync();
        // The construction queue only renders on dorf1/dorf2 (source of truth). If the current page
        // has none and we are not already on a village overview, read it on dorf2. Some build
        // pages can otherwise report an empty queue and the slot gate wrongly thinks a slot is free.
        if (raw.Count == 0
            && allowNavigationToBuildings
            && !IsCurrentUrlForPath(Paths.Buildings)
            && !IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            raw = await ReadActiveConstructionsOnCurrentPageAsync();
        }

        var readFromOverview =
            IsCurrentUrlForPath(Paths.Buildings) || IsCurrentUrlForPath(Paths.Resources);
        if (readFromOverview && raw.Count == 0)
        {
            // Empty is destructive state. Confirm it twice on a page that actually owns Travian's
            // construction queue before allowing desktop cache merge to clear a prior non-empty list.
            await Task.Delay(350, cancellationToken);
            raw = await ReadActiveConstructionsOnCurrentPageAsync();
            Notify($"[construction-status:verbose] confirmed empty overview queue with second DOM read.");
        }

        var result = raw
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i =>
            {
                var remainingSeconds = i.TimeLeftSeconds ?? TravianParsing.ParseDurationToSeconds(i.FinishAtText);
                return new ActiveConstruction(
                Kind: i.Kind switch
                {
                    "Resource" => ConstructionKind.Resource,
                    "Building" => ConstructionKind.Building,
                    _ => ConstructionKind.Unknown
                },
                Name: i.Name!,
                Level: i.Level,
                TimeLeftSeconds: remainingSeconds,
                FinishAtText: i.FinishAtText,
                Finish: remainingSeconds is > 0 ? TimerSnapshot.FromRemaining(remainingSeconds.Value) : null,
                SlotId: i.SlotId,
                Gid: i.Gid,
                Href: i.Href);
            })
            .ToList();

        _cachedActiveConstructions = result;
        _cachedActiveConstructionsAt = DateTimeOffset.UtcNow;
        _cachedActiveConstructionsFromOverview = readFromOverview;
        _lastActiveConstructionsFromOverview = readFromOverview;
        return result;

        async Task<List<ActiveConstructionJs>> ReadActiveConstructionsOnCurrentPageAsync()
        {
            var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const parseNumber = (value) => {
                if (value == null || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const readUrlParam = (href, names) => {
                if (!href) return null;
                try {
                  const url = new URL(href, window.location.href);
                  for (const name of names) {
                    const parsed = parseNumber(url.searchParams.get(name));
                    if (parsed != null) return parsed;
                  }
                } catch {
                  for (const name of names) {
                    const match = href.match(new RegExp(`[?&]${name}=(\\d{1,2})(?:\\D|$)`, 'i'));
                    if (match) return Number(match[1]);
                  }
                }
                return null;
              };
              const readElementNumber = (element, attrs, regexes) => {
                for (const attr of attrs) {
                  const parsed = parseNumber(element.getAttribute(attr));
                  if (parsed != null) return parsed;
                }
                const classText = String(element.className || '');
                for (const regex of regexes) {
                  const match = classText.match(regex);
                  if (match) return Number(match[1]);
                }
                return null;
              };
              const items = [];
              const lis = document.querySelectorAll('.boxes.buildingList ul li, .buildingList ul li');
              for (const li of lis) {
                const nameEl = li.querySelector('.name');
                if (!nameEl) continue;
                const fullName = (nameEl.textContent || '').replace(/\s+/g, ' ').trim();
                if (!fullName) continue;

                const lvlEl = nameEl.querySelector('.lvl');
                const lvlText = (lvlEl?.textContent || '').trim();
                const lvlMatch = lvlText.match(/(\d{1,3})/);
                const level = lvlMatch ? Number(lvlMatch[1]) : null;
                const baseName = lvlEl ? fullName.replace(lvlText, '').trim() : fullName;

                const timer = li.querySelector('.timer, [counting="down"]');
                let timeLeft = null;
                if (timer) {
                  const v = timer.getAttribute('value') || timer.getAttribute('data-value');
                  if (v && !isNaN(Number(v))) timeLeft = Number(v);
                }
                const finishText = (li.querySelector('.buildDuration')?.textContent || '').replace(/\s+/g, ' ').trim();

                const resourceNames = /(woodcutter|clay\s*pit|iron\s*mine|crop\s*land|cropland|skogshugg|lerg|j[äa]rng|s[äa]desf|holzf[äa]ller|lehmgrube|eisenmine|getreidefarm|bois|argile|fer|c[ée]r[ée]ales)/i;
                let kind = 'Unknown';
                if (resourceNames.test(baseName)) kind = 'Resource';
                else if (baseName) kind = 'Building';

                const link = li.querySelector('a[href*="build.php"], a[href*="dorf1.php"], a[href*="dorf2.php"]');
                const href = link ? (link.getAttribute('href') || '') : '';
                const slotId =
                  readUrlParam(href, ['id', 'a'])
                  ?? readElementNumber(li, ['data-aid', 'data-slot', 'data-slot-id', 'data-building-slot-id', 'data-id'], [/\baid(\d{1,2})\b/i, /\ba(\d{1,2})\b/i]);
                const gid =
                  readUrlParam(href, ['gid'])
                  ?? readElementNumber(li, ['data-gid', 'data-building-gid', 'data-type'], [/\bg(\d{1,2})\b/i]);

                items.push({ kind, name: baseName, level, timeLeftSeconds: timeLeft, finishAtText: finishText, slotId, gid, href: href || null });
              }
              return JSON.stringify(items);
            }
            """);

            return string.IsNullOrWhiteSpace(rawJson)
                ? new List<ActiveConstructionJs>()
                : JsonSerializer.Deserialize<List<ActiveConstructionJs>>(rawJson) ?? new List<ActiveConstructionJs>();
        }
    }

    public async Task<ConstructionSlotStatus> EvaluateConstructionSlotsAsync(
        string tribe,
        bool travianPlusActive,
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true)
    {
        LogFunctionStarted();
        var active = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings);
        return ConstructionSlots.Compute(active, tribe, travianPlusActive);
    }

    private async Task<(string Tribe, bool PlusActive)> GetCachedTribeAndPlusAsync(CancellationToken cancellationToken)
    {
        // Tribe is immutable for an account — cache for the entire session, only cleared on logout.
        var tribe = _sessionTribe ?? _cachedTribe;
        if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            tribe = await ReadTribeAsync(cancellationToken);
            _cachedTribe = tribe;
            if (!string.IsNullOrWhiteSpace(tribe) && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                _sessionTribe = tribe;
            }
            _cachedTribePlusAt = DateTimeOffset.UtcNow;
        }

        var plusActive = await IsTravianPlusActiveAsync(cancellationToken);
        if (_cachedTravianPlusActive != plusActive)
        {
            Notify($"[plus] active={plusActive} (changed)");
            _cachedTravianPlusActive = plusActive;
        }
        return (tribe!, plusActive);
    }

    // Non-blocking pre-flight check. Returns null if a slot is free for `kind`, otherwise
    // a defer message containing queue_wait_seconds=N for the program queue to pick up.
    // Use this instead of WaitForConstructionSlotIfBusyAsync when you want the task to be
    // re-queued by the desktop auto-queue rather than sleep inside the worker call.
    internal async Task<string?> CheckQueueOrDeferAsync(
        ConstructionKind kind,
        int slotId,
        int upgrades,
        CancellationToken cancellationToken,
        bool allowNavigationToBuildings = true)
    {
        var (tribe, plusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        if (!allowNavigationToBuildings)
        {
            InvalidateActiveConstructionsCache();
        }

        var status = await EvaluateConstructionSlotsAsync(tribe, plusActive, cancellationToken, allowNavigationToBuildings);
        var canStart = kind == ConstructionKind.Resource ? status.CanStartResource : status.CanStartBuilding;
        if (canStart)
        {
            return null;
        }

        var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);
        var relevantWait = (isRomans
                ? status.Active.Where(a => kind == ConstructionKind.Resource
                    ? a.Kind == ConstructionKind.Resource
                    : a.Kind != ConstructionKind.Resource)
                : status.Active)
            .Where(a => a.TimeLeftSeconds is int v && v > 0)
            .Select(a => a.TimeLeftSeconds!.Value)
            .DefaultIfEmpty(status.ShortestWaitSeconds ?? 0)
            .Min();
        // When the page gave us an actual timer, trust it (+1s race buffer so we don't poll
        // before the slot frees). The 5s floor only matters when we had no live timer at all —
        // without it we'd thrash polling if relevantWait==0.
        var wait = relevantWait > 0 ? relevantWait + 1 : 5;
        var label = kind == ConstructionKind.Resource ? "Resource slot" : "Slot";
        return $"{label} {slotId}: build queue full ({status.ResourceSlotsUsed}/{status.ResourceSlotsMax} resource, {status.BuildingSlotsUsed}/{status.BuildingSlotsMax} building, plus={plusActive}). Deferring upgrade. Upgrades performed: {upgrades}. queue_wait_seconds={wait}";
    }

    public async Task<int> WaitForConstructionSlotIfBusyAsync(
        ConstructionKind kind,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        var (tribe, plusActive) = await GetCachedTribeAndPlusAsync(cancellationToken);
        var status = await EvaluateConstructionSlotsAsync(tribe, plusActive, cancellationToken);

        var canStart = kind == ConstructionKind.Resource ? status.CanStartResource : status.CanStartBuilding;
        if (canStart)
        {
            return 0;
        }

        var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);
        var relevantItems = isRomans
            ? status.Active.Where(a => kind == ConstructionKind.Resource
                ? a.Kind == ConstructionKind.Resource
                : a.Kind != ConstructionKind.Resource)
            : status.Active;

        var relevant = relevantItems
            .Where(a => a.TimeLeftSeconds is int v && v > 0)
            .Select(a => a.TimeLeftSeconds!.Value)
            .DefaultIfEmpty(0)
            .Min();

        var wait = relevant > 0 ? relevant : status.ShortestWaitSeconds ?? 0;
        if (wait <= 0)
        {
            return 0;
        }

        Notify($"Construction slot busy for {kind}; waiting {wait}s (tribe={tribe}, plus={plusActive}). queue_wait_seconds={wait}");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 1, 12 * 60 * 60)), cancellationToken);
        return wait;
    }

    public Task<string> ReadTribeOnlyAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        return ReadTribeAsync(cancellationToken);
    }

    public async Task<bool> IsTravianPlusActiveAsync(CancellationToken cancellationToken = default)
    {
        var state = await EvaluatePlusStateOnCurrentPageAsync(cancellationToken);
        Notify($"[plus:verbose] state='{state}' url='{_page.Url}'");

        // Source of truth is dorf1/dorf2: the village quick-links bar and the link-list edit button
        // both reflect Plus there (green=on, gold=off). Other pages (e.g. build.php with Plus active)
        // can be inconclusive, so re-read on dorf2 before falling back.
        if (state == PlusState.Unknown
            && !IsCurrentUrlForPath(Paths.Resources)
            && !IsCurrentUrlForPath(Paths.Buildings))
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            state = await EvaluatePlusStateOnCurrentPageAsync(cancellationToken);
            Notify($"[plus:verbose] dorf2 re-read state='{state}' url='{_page.Url}'");
        }

        // Conservative fallback: only a positive "on" signal counts as Plus active. Anything
        // unknown is treated as inactive so we never over-fill the build queue (1 slot, not 2).
        return state == PlusState.On;
    }

    private static class PlusState
    {
        public const string On = "on";
        public const string Off = "off";
        public const string Unknown = "unknown";
    }

    // Reads a tri-state Plus signal ("on"/"off"/"unknown") from the current page using verified,
    // language-independent markup. Never defaults to "on".
    private async Task<string> EvaluatePlusStateOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading Travian Plus status.", cancellationToken);

        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const hasClass = (el, c) => (el.className || '').toString().split(/\s+/).includes(c);

                  // 1) Village quick-links bar (dorf1/dorf2). Present in BOTH states; only the color
                  //    differs: green = Plus active, gold = Plus inactive. (Verified against live DOM.)
                  const quickLinks = Array.from(document.querySelectorAll('a[data-dragid^="villageQuickLinks"]'));
                  if (quickLinks.length > 0) {
                    if (quickLinks.some(node => hasClass(node, 'green'))) return 'on';
                    if (quickLinks.some(node => hasClass(node, 'gold'))) return 'off';
                  }

                  // 2) Link-list edit button in the sidebar (village pages). green + linklist.php = Plus
                  //    active; gold + a PlusDialog upsell onclick = Plus inactive. (Verified.)
                  const edit = document.querySelector('#sidebarBoxLinklist a.edit, #sidebarBoxLinklist a.layoutButton.edit');
                  if (edit) {
                    const onclick = edit.getAttribute('onclick') || '';
                    if (hasClass(edit, 'gold') || /PlusDialog/.test(onclick)) return 'off';
                    if (hasClass(edit, 'green')) return 'on';
                  }

                  // 3) Build page (build.php): the 2nd queue slot is advertised as a locked Plus feature
                  //    only when Plus is inactive. (Verified: `.plusAdvertising` with featureKey 'buildingQueue'.)
                  if (document.querySelector('.plusAdvertising')) return 'off';

                  return 'unknown';
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return PlusState.Unknown;
        }
    }


    private async Task<UpgradeAttemptResult> AnalyzeUpgradeActionabilityAsync(
        int slotId,
        CancellationToken cancellationToken,
        bool performClick,
        bool skipNavigationIfOnExpectedSlot = false)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!skipNavigationIfOnExpectedSlot || !TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
                {
                    await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
                }
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the upgrade page.", cancellationToken);
                await EnsureLoggedInAsync();
                await EnsureExpectedBuildSlotPageAsync(slotId, "analyze upgrade", cancellationToken);
                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay

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

                      const parseTargetLevel = (raw) => {
                        const match = clean(raw || '').match(/upgrade\s+to\s+level\s+(\d{1,3})/i);
                        return match ? Number(match[1]) : null;
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

                      const readServerNow = () => {
                        const candidates = ['#servertime .timeStandard', '#servertime', '.serverTime'];
                        for (const sel of candidates) {
                          const el = document.querySelector(sel);
                          const t = clean(el?.textContent || '');
                          const m = t.match(/(\d{1,2}):(\d{2}):(\d{2})/);
                          if (m) {
                            const now = new Date();
                            now.setHours(Number(m[1]), Number(m[2]), Number(m[3]), 0);
                            return now;
                          }
                        }
                        return new Date();
                      };

                      const parseClockTimeToSeconds = (raw) => {
                        const text = clean(raw || '');
                        if (!text) return null;
                        const tomorrow = /(tomorrow|morgen|imorgon|i\s*morgon|demain|domani|ma[ñn]ana|jutro)/i.test(text);
                        const today = /(today|heute|idag|i\s*dag|aujourd|oggi|hoy|dzisiaj)/i.test(text);
                        if (!today && !tomorrow) return null;
                        const m = text.match(/(\d{1,2}):(\d{2}):(\d{2})/);
                        if (!m) return null;
                        const target = readServerNow();
                        target.setHours(Number(m[1]), Number(m[2]), Number(m[3]), 0);
                        if (tomorrow) target.setDate(target.getDate() + 1);
                        let diff = Math.round((target.getTime() - readServerNow().getTime()) / 1000);
                        if (today && diff < 0) diff += 86400;
                        return diff > 0 ? diff : null;
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
                          const clockSeconds = parseClockTimeToSeconds(source);
                          if (clockSeconds && clockSeconds > 0) {
                            return clockSeconds;
                          }
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
                        const officialPrimary = candidate.inOfficialPrimarySection;
                        if (normalizedProfile === 'strict_green') {
                          return (officialPrimary ? 8 : 0) + (green ? 6 : 0) + (container ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        if (normalizedProfile === 'container_first') {
                          return (officialPrimary ? 8 : 0) + (container ? 6 : 0) + (green ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        if (normalizedProfile === 'aggressive') {
                          return (officialPrimary ? 8 : 0) + (signalClass ? 4 : 0) + (container ? 3 : 0) + (green ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        return (officialPrimary ? 8 : 0) + (green ? 3 : 0) + (container ? 2 : 0) + (upgradeText ? 1 : 0);
                      };

                      const candidates = Array.from(document.querySelectorAll('button, input[type="submit"], input[type="button"], a, div.addHoverClick, div.button-container'));
                      const picked = [];
                      const clickOrder = [];
                      let hasMasterBuilderOnlyControl = false;

                      for (let candidateIndex = 0; candidateIndex < candidates.length; candidateIndex += 1) {
                        const element = candidates[candidateIndex];
                        const text = textOf(element).toLowerCase();
                        const classes = clean(element.className || '').toLowerCase();
                        const href = (element.getAttribute('href') || '').toLowerCase();
                        const form = element.closest('form');
                        const formAction = (form ? form.getAttribute('action') : '') || '';
                        const control = element.closest('button, input[type="submit"], input[type="button"], a, div.addHoverClick');
                        const controlText = control ? textOf(control).toLowerCase() : '';
                        const controlClasses = control ? clean(control.className || '').toLowerCase() : '';
                        const onclick = `${(element.getAttribute('onclick') || '')} ${(control ? control.getAttribute('onclick') || '' : '')}`.toLowerCase();
                        const combined = `${text} ${classes} ${controlText} ${controlClasses} ${href} ${formAction} ${onclick}`;
                        const displayText = text || controlText;
                        const disabled = !!(
                          element.disabled
                          || classes.includes('disabled')
                          || element.getAttribute('aria-disabled') === 'true'
                          || (control && control.disabled)
                          || controlClasses.includes('disabled')
                          || (control && control.getAttribute('aria-disabled') === 'true')
                        );
                        const inOfficialPrimarySection = !!element.closest('.upgradeButtonsContainer .section1');
                        const inOfficialSpeedupSection = !!element.closest('.upgradeButtonsContainer .section2');
                        const isMasterBuilder = combined.includes('master builder') || combined.includes('buildmaster');
                        const isGold = isMasterBuilder || combined.includes('gold') || combined.includes('npc') || combined.includes('instant') || combined.includes('exchange') || combined.includes('sharing resources') || combined.includes('share resources');
                        // Official payment-wizard decoy: the green "Open shop" button (onclick=openPaymentWizard)
                        // is not a build control but matches the bare 'green' signal below. Clicking it opens a
                        // modal whose #dialogOverlay then intercepts all later clicks → upgrade-click timeout loop.
                        // Match the locale-independent onclick first, then the English text/value as a fallback.
                        const isPaymentShop = combined.includes('openpaymentwizard') || combined.includes('paymentwizard') || combined.includes('open shop');
                        // This scanner runs only for upgrades. A "Construct building" control means the slot is
                        // empty / shows the construction-choice page — never a valid upgrade candidate. Its text
                        // contains "build", so without this guard it leaks through as a false CanUpgrade. Match on
                        // the button TEXT only: both construct and upgrade buttons share onclick `action=build`,
                        // so the action keyword cannot distinguish them — the text ("Construct building" vs
                        // "Upgrade to level N") is the reliable signal.
                        const isConstruct = /construct\s+building/i.test(displayText);
                        // Village-map level badges (e.g. dorf2 building-slot overlays
                        // `<a class="level colorlayer good aidNN <tribe>" href="build.php?id=N">`) link to
                        // build.php but only carry the slot's current level number — they are NOT upgrade
                        // controls. They leak in via the bare `href build.php` signal below and, being the
                        // only "candidate" found, mask the real blocked state into a false CanUpgrade →
                        // misleading "could not find Upgrade to level N" alarm. The `colorlayer` overlay class
                        // never appears on a real upgrade button, so exclude it.
                        const isLevelBadge = classes.includes('colorlayer') || controlClasses.includes('colorlayer');
                        // The hero adventure button is green (`... adventure green attention`) but unrelated to
                        // building upgrades; it leaks in via the bare `green` signal. The `adventure` class
                        // never appears on a real upgrade button, so exclude it.
                        const isAdventure = classes.includes('adventure') || controlClasses.includes('adventure');
                        const isSpeedup = inOfficialSpeedupSection || classes.includes('purple') || controlClasses.includes('purple') || classes.includes('videofeaturebutton') || controlClasses.includes('videofeaturebutton') || combined.includes('videoFeature') || combined.includes('videofeature') || combined.includes('faster');
                        const inUpgradeContainer = !!element.closest('.upgradeBuilding, .contract, .contractWrapper, .build_details, .buildingWrapper, #contract, form[action*="build.php"]');
                        const hasUpgradeSignals =
                          inOfficialPrimarySection
                          || classes.includes('green')
                          || controlClasses.includes('green')
                          || classes.includes('upgrade')
                          || controlClasses.includes('upgrade')
                          || classes.includes('build')
                          || controlClasses.includes('build')
                          || classes.includes('contract')
                          || controlClasses.includes('contract')
                          || classes.includes('addhoverclick')
                          || controlClasses.includes('addhoverclick')
                          || href.includes('build.php')
                          || formAction.includes('build.php')
                          || (inUpgradeContainer && /upgrade\s+to\s+level|upgrade|build/i.test(displayText))
                          || /upgrade\s+to\s+level/i.test(displayText);

                        if (isMasterBuilder) {
                          hasMasterBuilderOnlyControl = true;
                        }

                        const looksLikePrimaryNoise = inOfficialPrimarySection
                          && !/upgrade|construct|build/i.test(displayText)
                          && !href.includes('action=build')
                          && !formAction.includes('build.php');

                        if (!hasUpgradeSignals || isGold || isPaymentShop || isConstruct || isLevelBadge || isAdventure || isSpeedup || looksLikePrimaryNoise || displayText.length === 0) {
                          continue;
                        }

                        picked.push({
                          text: displayText.slice(0, 120),
                          classes: classes.slice(0, 120),
                          disabled,
                          inUpgradeContainer,
                          inOfficialPrimarySection
                        });

                        if (!disabled) {
                          clickOrder.push({ candidateIndex, text: displayText, targetLevel: parseTargetLevel(displayText), classes: `${classes} ${controlClasses}`, inUpgradeContainer, inOfficialPrimarySection });
                        }
                      }

                      clickOrder.sort((a, b) => score(b) - score(a));

                      // Official "not enough resources" hard block: the .upgradeBlocked panel
                      // replaces the green upgrade button with an errorMessage ("Enough resources
                      // on DD.MM. at HH:MM") plus only master-builder/exchange (gold) controls.
                      // When this panel is present we must NOT click any leftover candidate — that
                      // produced an endless click/navigate spam loop. Take the precise wait from the
                      // panel's embedded countdown timer (value=<seconds>) so the task defers cleanly.
                      const upgradeBlockedEl = document.querySelector('.upgradeBlocked');
                      if (upgradeBlockedEl) {
                        const blockText = clean(upgradeBlockedEl.textContent || '').toLowerCase();
                        const isResourceBlock = /enough\s*resources\s*on|not\s*enough|insufficient|missing\s*resources/i.test(blockText);
                        const isStorageCapacityBlock =
                          /extend\s+(?:the\s+)?(?:warehouse|granary|silo)/i.test(blockText)
                          || /(?:warehouse|granary|silo)(?:\s+and\s+(?:warehouse|granary|silo))?\s+first/i.test(blockText);
                        if (isResourceBlock || isStorageCapacityBlock) {
                          let blockedSeconds = null;
                          const timerEl = upgradeBlockedEl.querySelector('.timer[value], .timer[data-value]');
                          if (timerEl) {
                            const v = Number(timerEl.getAttribute('value') || timerEl.getAttribute('data-value'));
                            if (Number.isFinite(v) && v > 0) {
                              blockedSeconds = v;
                            }
                          }
                          if (blockedSeconds === null) {
                            blockedSeconds = detectResourceWaitSeconds();
                          }
                          return JSON.stringify({
                            outcome: 'BlockedByResources',
                            reason: isStorageCapacityBlock
                              ? 'Upgrade blocked: storage capacity is too low (upgradeBlocked panel).'
                              : 'Upgrade blocked: not enough resources yet (upgradeBlocked panel).',
                            detectedMaxLevel: detectMaxLevel(),
                            queueWaitSeconds: blockedSeconds,
                            summary: picked.slice(0, 8)
                          });
                        }
                      }

                      if (clickOrder.length > 0) {
                        return JSON.stringify({
                          outcome: 'CanUpgrade',
                          reason: `Detected candidate '${clickOrder[0].text.slice(0, 80)}'`,
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds: detectQueueWaitSeconds(),
                          detectedTargetLevel: clickOrder[0].targetLevel,
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

                      if (hasMasterBuilderOnlyControl) {
                        const queueWaitSeconds = detectQueueWaitSeconds();
                        return JSON.stringify({
                          outcome: 'BlockedByQueue',
                          reason: 'Only master builder construction is available; normal build queue is full.',
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

                var outcome = TravianParsing.ParseUpgradeOutcome(parsed?.Outcome);
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
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                        .WaitAsync(cancellationToken);
                }, cancellationToken: cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after upgrade actionability analysis.", cancellationToken);

                return new UpgradeAttemptResult(
                    Outcome: outcome,
                    Reason: reason,
                    DetectedMaxLevel: parsed?.DetectedMaxLevel,
                    QueueWaitSeconds: parsed?.QueueWaitSeconds,
                    DetectedTargetLevel: parsed?.DetectedTargetLevel,
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

    private async Task<int> ResolveBuildingMaxLevelAsync(Building building, int slotId, CancellationToken cancellationToken)
    {
        var configured = BuildingNames.MaxLevelFor(building);
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
        string buildingName,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int? gid,
        int targetLevel,
        CancellationToken cancellationToken)
    {
        var queueFingerprintBefore = BuildQueueFingerprints.Identity(buildQueueBefore);
        // Tight poll: most clicks register within ~250ms. Two iterations covers slow pages
        // without burning a second on the happy path. Each iteration runs three cheap reads:
        // queue, active constructions, and (NEW) the slot level itself — the latter catches
        // instant-complete servers where the upgrade finishes before the queue can hold it.
        var stalePageReloads = 0;
        for (var i = 0; i < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);

            // Each poll iteration must observe FRESH state — bypass the ReadActiveConstructions
            // cache so a change since the last 800ms window isn't hidden by a stale result.
            InvalidateActiveConstructionsCache();

            // If Travian's own "auto-reload failed" marker is visible on the page (a build timer
            // ticked past zero but the page never reloaded), our level / queue reads below would
            // see pre-completion state. Force a reload and re-poll. Don't count this as a "did not
            // confirm" attempt — the iteration burned its budget waiting on a stale DOM. Cap the
            // retries at 2 so a persistent marker (server problem, blocked reload) can't loop forever.
            if (await IsPageMarkedStaleAsync())
            {
                if (stalePageReloads >= 2)
                {
                    Notify("Build queue page still marked stale after 2 reloads; continuing with possibly stale state.");
                }
                else
                {
                    stalePageReloads += 1;
                    Notify($"Build queue page is stale (Travian auto-reload failed); reloading (attempt {stalePageReloads}/2).");
                    await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
                        .WaitAsync(cancellationToken);
                    InvalidateActiveConstructionsCache();
                    i--; // re-run this iteration after the reload
                    continue;
                }
            }

            // Check 1: did the slot's level advance? On fast servers the build completes before
            // the queue can show it; this read is the only reliable success signal there.
            var observedLevel = await TryReadSlotLevelOnCurrentPageAsync(slotId);
            if (observedLevel is int level && level > previousLevel)
            {
                return new UpgradeProgressResult(true, true, $"level advanced to {level}");
            }

            var queueItems = await ReadBuildQueueAsync(cancellationToken);
            var queueFingerprintAfter = BuildQueueFingerprints.Identity(queueItems);
            var targetQueueItem = BuildQueueFingerprints.FindNewTargetBuilding(buildQueueBefore, queueItems, buildingName, slotId, gid, targetLevel);
            if (targetQueueItem is not null)
            {
                return new UpgradeProgressResult(false, true, $"build queue contains slot {targetQueueItem.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} {buildingName}");
            }

            var newQueueItem = BuildQueueFingerprints.FindNewBuildingByName(buildQueueBefore, queueItems, buildingName);
            if (newQueueItem is not null)
            {
                return new UpgradeProgressResult(false, true, $"build queue added {buildingName}");
            }

            if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
            {
                Notify($"Build progress check for slot {slotId}: queue changed but no {buildingName} entry was found.");
            }

            var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken);
            var matchingActiveConstruction = activeConstructions.FirstOrDefault(item =>
                item.Kind != ConstructionKind.Resource
                && ActiveConstructionMatchesTarget(item, slotId, gid, targetLevel));
            if (matchingActiveConstruction is not null)
            {
                return new UpgradeProgressResult(false, true, $"active construction detected for slot {matchingActiveConstruction.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} {matchingActiveConstruction.Name}");
            }
        }

        // Final level probe: a fast-server upgrade might finish during the 500ms poll window
        // without the queue ever rendering. Promote that to a confirmed-advance instead of the
        // "did not confirm" defer path so we don't burn a full retry cycle on a successful click.
        var finalLevel = await TryReadSlotLevelOnCurrentPageAsync(slotId);
        if (finalLevel is int finalValue && finalValue > previousLevel)
        {
            return new UpgradeProgressResult(true, true, $"level advanced to {finalValue} (post-poll)");
        }

        return new UpgradeProgressResult(false, false, "no queue or level change");
    }

    private static bool ActiveConstructionMatchesTarget(
        ActiveConstruction item,
        int slotId,
        int? gid,
        int targetLevel)
    {
        if (item.SlotId != slotId)
        {
            return false;
        }

        if (gid is int expectedGid && item.Gid is int actualGid && actualGid != expectedGid)
        {
            return false;
        }

        return item.Level is not int level || level >= targetLevel;
    }

    /// <summary>
    /// Last-chance success probe before triggering the "did not confirm" defer cycle.
    /// Navigates to dorf2 (if not already there) and reads the slot's level directly. On
    /// fast servers the upgrade lands so quickly that queue polling misses it; this catches
    /// those cases for the cost of one nav rather than burning a full ~4s retry tick.
    /// </summary>
    private async Task<int?> ProbeSlotLevelOnDorf2Async(int slotId, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentUrlForPath(Paths.Buildings))
            {
                await GotoAsync(Paths.Buildings, cancellationToken);
            }
            return await TryReadSlotLevelOnCurrentPageAsync(slotId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the visible level for <paramref name="slotId"/> from whichever Travian page is
    /// currently loaded — works on the slot's build.php (header) and dorf2 (slot label).
    /// Returns null if no level indicator is found.
    /// </summary>
    private async Task<int?> TryReadSlotLevelOnCurrentPageAsync(int slotId)
    {
        try
        {
            return await _page.EvaluateAsync<int?>(
                """
                (slotId) => {
                  // 1. Slot's own build page: title like "Main Building Level 5".
                  const url = location.href.toLowerCase();
                  if (url.includes(`build.php`) && url.includes(`id=${slotId}`)) {
                    const header = document.querySelector('h1.titleInHeader, h1');
                    const text = (header?.textContent || '').trim();
                    const m = text.match(/level\s*(\d{1,2})/i);
                    if (m) return Number(m[1]);
                  }

                  // 2. dorf2 slot: <div class="buildingSlot aN"><a class="level">5</a></div>
                  //    or "<div class='buildingSlot aN'><div class='labelLayer'>5</div></div>".
                  const slot =
                       document.querySelector(`div.buildingSlot.a${slotId}`)
                    || document.querySelector(`div.buildingSlot[data-aid="${slotId}"]`)
                    || document.querySelector(`#villageContent div[class*=" a${slotId} "]`)
                    || document.querySelector(`#villageContent div[class$=" a${slotId}"]`);
                  if (slot) {
                    const label = slot.querySelector('.level, .labelLayer, span');
                    const text = (label?.textContent || slot.textContent || '').trim();
                    const m = text.match(/\d{1,2}/);
                    if (m) return Number(m[0]);
                  }

                  return null;
                }
                """,
                slotId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Confirms whether the slot's build page already shows an EXISTING building (not a construct-choice
    /// page) and returns its name + level. Used by construct to detect a slot that is already occupied
    /// (e.g. a special fixed slot like Rally Point/Wall, or a building that was built since the task was
    /// queued) so the task can be removed instead of failing on the missing construct-choice DOM.
    /// Returns null when the page still offers construct choices or no built-building header is present.
    /// </summary>
    private async Task<(string Name, int Level)?> TryReadExistingBuildingOnSlotBuildPageAsync(int slotId)
    {
        try
        {
            var result = await _page.EvaluateAsync<string?>(
                """
                (slotId) => {
                  const url = location.href.toLowerCase();
                  if (!url.includes('build.php') || !url.includes(`id=${slotId}`)) return null;
                  // A construct-choice page offers new buildings — never treat that as "already built".
                  if (document.querySelector('[id^="contract_building"], #contract_building')) return null;
                  const header = document.querySelector('h1.titleInHeader, h1');
                  const text = (header?.textContent || '').trim();
                  const m = text.match(/^(.*?)\s*(?:level|stufe|nivå)\s*(\d{1,2})/i);
                  if (!m) return null;
                  const level = Number(m[2]);
                  if (!(level >= 1)) return null;
                  return JSON.stringify({ name: m[1].trim(), level });
                }
                """,
                slotId);
            if (string.IsNullOrWhiteSpace(result))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(result);
            var name = doc.RootElement.GetProperty("name").GetString() ?? string.Empty;
            var level = doc.RootElement.GetProperty("level").GetInt32();
            return (name, level);
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> ReadQueuedBuildingWaitSecondsAsync(
        string buildingName,
        int fallbackSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = await ReadActiveConstructionsAsync(cancellationToken);
            var buildingTimers = active
                .Where(item => item.Kind != ConstructionKind.Resource && item.TimeLeftSeconds is int seconds && seconds > 0)
                .ToList();
            if (buildingTimers.Count == 0)
            {
                return UpgradeMath.ComputeUpgradeWaitSeconds(fallbackSeconds);
            }

            var matchingTimers = buildingTimers
                .Where(item => BuildingNames.Same(item.Name, buildingName))
                .Select(item => item.TimeLeftSeconds!.Value)
                .ToList();
            if (matchingTimers.Count > 0)
            {
                return UpgradeMath.ComputeUpgradeWaitSeconds(matchingTimers.Min());
            }

            return UpgradeMath.ComputeUpgradeWaitSeconds(buildingTimers.Min(item => item.TimeLeftSeconds!.Value));
        }
        catch
        {
            return UpgradeMath.ComputeUpgradeWaitSeconds(fallbackSeconds);
        }
    }

    private async Task<int> ReadHighestKnownQueuedBuildingLevelAsync(
        string buildingName,
        int currentLevel,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = await ReadActiveConstructionsAsync(cancellationToken);
            var highestQueuedLevel = active
                .Where(item => item.Kind != ConstructionKind.Resource && BuildingNames.Same(item.Name, buildingName))
                .Select(item => item.Level ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            return Math.Max(currentLevel, highestQueuedLevel);
        }
        catch
        {
            return currentLevel;
        }
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
        if (gid is 38 or 39)
        {
            throw new InvalidOperationException($"{name} requires building plans and is not supported yet.");
        }

        var existing = status.Buildings
            .Where(building => building.Gid == gid || BuildingNames.Same(building.Name, name))
            .ToList();
        var duplicateAllowed = gid is 23 or 38 or 39;
        var wallGid = gid is 31 or 32 or 33 or 42 or 43;
        if ((gid is 29 or 30) && status.IsCapital == true)
        {
            throw new InvalidOperationException($"{name} cannot be built in the capital.");
        }

        if (BuildingCatalogService.DuplicateRequiredExistingLevelFor(gid) is int duplicateRequiredLevel)
        {
            if (existing.Count > 0)
            {
                var highest = existing
                    .Where(building => building.Level is not null)
                    .Select(building => building.Level!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                if (highest < duplicateRequiredLevel)
                {
                    throw new InvalidOperationException($"{name} can only be duplicated after an existing one reaches level {duplicateRequiredLevel}.");
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

    private static List<(string name, int level)> MissingBuildingRequirements(VillageStatus status, int gid)
    {
        var missing = new List<(string name, int level)>();
        foreach (var requirement in BuildingCatalogService.RequirementsFor(gid))
        {
            var current = BuildingNames.LevelByName(status, requirement.Name);
            if (current < requirement.Level)
            {
                missing.Add((requirement.Name, requirement.Level));
            }
        }

        return missing;
    }

    internal static IReadOnlyList<Building> ParseBuildingOverviewHtmlForTests(string html)
    {
        var slots = BuildingDomParser.ExtractBuildingSlotHtml(html)
            .Select((slotHtml, index) =>
            {
                var className = BuildingDomParser.ReadAttribute(slotHtml, "class") ?? string.Empty;
                var labelText = BuildingDomParser.CleanHtmlText(Regex.Match(slotHtml, @"<div\b[^>]*class=[""'][^""']*\blabelLayer\b[^""']*[""'][^>]*>(?<text>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["text"].Value);
                var link = Regex.Match(slotHtml, @"<a\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["attrs"].Value;
                var dataName = BuildingDomParser.ReadAttribute(slotHtml, "data-name") ?? string.Empty;
                var dataLevel = BuildingDomParser.ReadAttribute(link, "data-level") ?? BuildingDomParser.ReadAttribute(slotHtml, "data-level") ?? string.Empty;
                return new BuildingOverviewSlotSnapshot
                {
                    Index = index,
                    ClassName = className,
                    OuterHtml = slotHtml,
                    LevelText = labelText,
                    DataLevelText = dataLevel,
                    DataNameText = dataName,
                    Text = BuildingDomParser.CleanHtmlText(slotHtml),
                    OccupiedEvidence = !string.IsNullOrWhiteSpace(link)
                        || !string.IsNullOrWhiteSpace(dataName)
                        || Regex.IsMatch(className, @"\bg\d{1,2}\b", RegexOptions.IgnoreCase),
                };
            })
            .ToList();

        return ParseBuildingOverviewScan(slots)
            .Buildings
            .Values
            .OrderBy(item => item.SlotId)
            .Select(item => new Building(
                item.SlotId,
                item.BuildingName,
                item.LevelKnown || !item.HasOccupancyEvidence ? item.Level : null,
                null,
                ParseGidFromBuildingCode(item.BuildingCode)))
            .ToList();
    }

    private async Task EnsureExpectedBuildSlotPageAsync(int slotId, string operationLabel, CancellationToken cancellationToken = default)
    {
        if (!TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
        {
            // A prior read/redirect in the upgrade flow (ReadActiveConstructionsAsync / ReadBuildQueueAsync
            // / a post-click redirect) can leave us on dorf2.php?id=slot, which carries the same id= param
            // as build.php?id=slot. Re-open the slot so the upgrade click targets the build page instead of
            // silently running on the village overview. (Official build.php?id=N also adds &gid=.)
            await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
            await EnsureLoggedInAsync();
            if (!TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
            {
                throw new InvalidOperationException(
                    $"{operationLabel} expected build.php?id={slotId}, but current url is '{_page.Url}'.");
            }
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var hasBuildContext = await WaitForBuildSlotContextAsync(slotId, attempt == 1 ? 5000 : 3000, cancellationToken);
            if (hasBuildContext)
            {
                return;
            }

            if (attempt == 1)
            {
                Notify($"{operationLabel} expected build controls for slot {slotId}, but the page was not ready. Reloading the current build page once.");
                await ReloadOrGotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
                await EnsureLoggedInAsync();
            }
        }

        throw new InvalidOperationException(
            $"{operationLabel} expected a build slot context, but required build controls were not found.");
    }

    private async Task EnsureExpectedConstructChoicePageAsync(
        int slotId,
        int gid,
        string constructUrl,
        string operationLabel,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = false;
            try
            {
                ready = await _page.EvaluateAsync<bool>(
                    """
                    ({ slotId, gid }) => {
                      const match = window.location.href.match(/[?&]id=(\d+)/);
                      const currentSlot = match ? Number(match[1]) : null;
                      if (currentSlot !== slotId) return false;
                      return !!document.querySelector(
                        `#contract_building${gid}, #building${gid}, [data-gid="${gid}"], #contract_building, [id^="contract_building"]`
                      );
                    }
                    """,
                    new { slotId, gid });
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex))
            {
                Notify($"{operationLabel} construct-page verification hit transient navigation: {ex.Message}");
            }

            if (ready)
            {
                return;
            }

            Notify($"{operationLabel} expected construct choices for slot {slotId}/gid {gid}, but current url is '{_page.Url}'. Reopening attempt {attempt}/2.");
            await GotoAsync(constructUrl, cancellationToken);
            await EnsureLoggedInAsync();
            await PauseForManualStepIfVisibleAsync(
                $"Manual verification while reopening construct page for slot {slotId}.",
                cancellationToken);
        }

        throw new InvalidOperationException(
            $"{operationLabel} could not load the construct-choice page for slot {slotId}/gid {gid}; current url is '{_page.Url}'.");
    }

    private async Task<bool> WaitForBuildSlotContextAsync(int slotId, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.WaitForFunctionAsync(
                """
                ({ slotId }) => {
                  // Must be the build page itself — dorf2.php?id=slot carries the same id= and even has
                  // build.php links, so the id+selector check alone would falsely pass on the overview.
                  if (!/build\.php/i.test(window.location.pathname)) return false;
                  const currentSlot = (() => {
                    const match = window.location.href.match(/[?&]id=(\d+)/);
                    return match ? Number(match[1]) : null;
                  })();
                  if (currentSlot !== slotId) return false;
                  return !!document.querySelector(
                    '#build, #contract, .upgradeBuilding, .contractWrapper, .buildingWrapper, .build_details, a[href*="build.php?id="]'
                  );
                }
                """,
                new { slotId },
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private static readonly Dictionary<string, string> TravianBuildings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g1"] = "Woodcutter",
        ["g2"] = "Clay Pit",
        ["g3"] = "Iron Mine",
        ["g4"] = "Cropland",
        ["g5"] = "Sawmill",
        ["g6"] = "Brickyard",
        ["g7"] = "Iron Foundry",
        ["g8"] = "Grain Mill",
        ["g9"] = "Bakery",
        ["g10"] = "Warehouse",
        ["g11"] = "Granary",
        ["g13"] = "Smithy",
        ["g14"] = "Tournament Square",
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
        ["g34"] = "Stonemason's Lodge",
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
        ["g46"] = "Hospital",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> NormalizedBuildingCodesByName = new(() =>
    {
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in TravianBuildings)
        {
            var normalized = BuildingNames.Normalize(entry.Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (mappings.TryGetValue(normalized, out var existingCode)
                && !string.Equals(existingCode, entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                duplicates.Add(normalized);
                continue;
            }

            mappings[normalized] = entry.Key;
        }

        foreach (var duplicate in duplicates)
        {
            mappings.Remove(duplicate);
        }

        return mappings;
    });

    private static readonly Regex AidClassRegex = new(@"\baid(?<id>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FallbackSlotClassRegex = new(@"\ba(?<id>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingSlotQueryRegex = new(@"[?&](?:id|a)=(?<id>\d{1,2})(?:\D|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingSlotDataRegex = new(@"data-(?:aid|slot|slot-id|building-slot-id|id)\s*=\s*[""']?(?<id>\d{1,2})(?:[""'\s>]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingCodeClassRegex = new(@"\bg(?<gid>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingGidQueryRegex = new(@"[?&]gid=(?<gid>\d{1,2})(?:\D|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildingGidDataRegex = new(@"data-(?:gid|building-gid|type)\s*=\s*[""']?(?<gid>\d{1,2})(?:[""'\s>]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OverviewLevelRegex = new(@"\blevel\s*(?<level>\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions BuildingOverviewSnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private enum BuildingOverviewScanConfidence
    {
        Low,
        Medium,
        High,
    }

    private sealed class BuildingOverviewSlotSnapshot
    {
        public int Index { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public string OuterHtml { get; set; } = string.Empty;
        public string LevelText { get; set; } = string.Empty;
        public string DataLevelText { get; set; } = string.Empty;
        public string DataNameText { get; set; } = string.Empty;
        public string NameText { get; set; } = string.Empty;
        public string TitleText { get; set; } = string.Empty;
        public string AltText { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public bool OccupiedEvidence { get; set; }
    }

    private sealed class BuildingOverviewScanResult
    {
        public Dictionary<int, BuildingInfo> Buildings { get; init; } = new();
        public BuildingOverviewScanConfidence Confidence { get; init; }
        public int MissingBuildingCodeCount { get; init; }
        public int UnknownLevelCount { get; init; }
        public bool MissingMainBuilding { get; init; }
        public bool MissingRallyPoint { get; init; }
    }

    private sealed class BuildingInfo
    {
        public int SlotId { get; set; }
        public string BuildingCode { get; set; } = string.Empty;
        public string BuildingName { get; set; } = "Empty";
        public int Level { get; set; }
        public bool LevelKnown { get; set; }
        public bool HasOccupancyEvidence { get; set; }
    }

    private sealed class CurrentBuildPageSlotSnapshot
    {
        [JsonPropertyName("slotId")]
        public int? SlotId { get; set; }

        [JsonPropertyName("level")]
        public int? Level { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("gid")]
        public int? Gid { get; set; }

        [JsonPropertyName("hasBuildContext")]
        public bool HasBuildContext { get; set; }
    }

}
