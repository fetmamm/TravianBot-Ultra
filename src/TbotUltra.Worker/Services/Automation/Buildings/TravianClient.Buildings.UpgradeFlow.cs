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

            // Human-like defer before starting the next build (see MaybeGetConstructionHumanizeDeferAsync).
            var humanizeDefer = await MaybeGetConstructionHumanizeDeferAsync(ConstructionKind.Building, slotId, cancellationToken);
            if (humanizeDefer is not null)
            {
                return humanizeDefer;
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
            var constructFaster = await TryUseConstructFasterForBuildAsync(
                slotId,
                ParseGidFromBuildingCode(info.BuildingCode),
                buildingName,
                currentLevel,
                nextLevel,
                buildQueueBefore,
                durationSeconds,
                null,
                cancellationToken);
            var clicked = constructFaster.ActionRegistered;
            var usedConstructFasterVideo = constructFaster.BonusConfirmed;
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
                            await ApplyPacingDelayAsync(
                                _config.ActionPacingPageLoadMinSeconds,
                                _config.ActionPacingPageLoadMaxSeconds,
                                "page-load-pacing",
                                "after hero transfer reload",
                                cancellationToken);
                            var retryConstructFaster = await TryUseConstructFasterForBuildAsync(
                                slotId,
                                ParseGidFromBuildingCode(info.BuildingCode),
                                buildingName,
                                currentLevel,
                                nextLevel,
                                buildQueueBefore,
                                durationSeconds,
                                null,
                                cancellationToken);
                            clicked = retryConstructFaster.ActionRegistered;
                            usedConstructFasterVideo |= retryConstructFaster.BonusConfirmed;
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
                        return UpgradeResourceWaitCalculator.BuildBlockedResultMessage(snapshot);
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

                var waitMs = UpgradeResourceWaitCalculator.ComputePostActionWaitMs(durationSeconds);
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
            Notify(UpgradeResourceWaitCalculator.FormatLog(heroLimitSnapshot));
            return heroLimitSnapshot;
        }

        _heroTransferOverLimitWaitSeconds = null;

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
            var snapshot = UpgradeResourceWaitCalculator.BuildSnapshot(
                blockedLabel,
                required,
                currentResourcesFromStockBar,
                cachedProductionByHour,
                fallbackWaitSeconds,
                HasAnyProduction(cachedProductionByHour) ? "cached_production" : "page_timer",
                capacities.Warehouse,
                capacities.Granary,
                serverStorageBlockKind);
            Notify(UpgradeResourceWaitCalculator.FormatLog(snapshot));
            return snapshot;
        }

        var currentResources = await ReadResourcesAsync(cancellationToken);
        var productionByHour = await ReadResourceProductionPerHourAsync(cancellationToken);
        var liveSnapshot = UpgradeResourceWaitCalculator.BuildSnapshot(
            blockedLabel,
            required,
            currentResources,
            productionByHour,
            fallbackWaitSeconds,
            "estimated_from_page",
            capacities.Warehouse,
            capacities.Granary,
            serverStorageBlockKind);
        Notify(UpgradeResourceWaitCalculator.FormatLog(liveSnapshot));
        return liveSnapshot;
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
            return await _page.EvaluateAsync<bool>("() => !!document.querySelector('#production')");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private enum BuildPageState { Other, AtMaxLevel, WorkersBusy, EmptyConstructionSlot }

    private sealed record ConstructionPageAnalysis(
        BuildPageState State,
        UpgradeAttemptResult? UpgradeActionability,
        int DurationSeconds,
        int? PopulationDelta,
        bool LooksBlockedByResources,
        string? ConstructRequirementError);

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

            // Human-like defer before starting the next build (see MaybeGetConstructionHumanizeDeferAsync).
            var humanizeDefer = await MaybeGetConstructionHumanizeDeferAsync(ConstructionKind.Building, slotId, cancellationToken);
            if (humanizeDefer is not null)
            {
                return humanizeDefer;
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
            var constructFaster = await TryUseConstructFasterForBuildAsync(
                slotId,
                gid,
                buildingName,
                currentLevel,
                nextLevel,
                buildQueueBefore,
                durationSeconds,
                null,
                cancellationToken);
            var clicked = constructFaster.ActionRegistered;
            var usedConstructFasterVideo = constructFaster.BonusConfirmed;
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
                            await ApplyPacingDelayAsync(
                                _config.ActionPacingPageLoadMinSeconds,
                                _config.ActionPacingPageLoadMaxSeconds,
                                "page-load-pacing",
                                "after hero transfer reload",
                                cancellationToken);
                            var retryConstructFaster = await TryUseConstructFasterForBuildAsync(
                                slotId,
                                gid,
                                buildingName,
                                currentLevel,
                                nextLevel,
                                buildQueueBefore,
                                durationSeconds,
                                null,
                                cancellationToken);
                            clicked = retryConstructFaster.ActionRegistered;
                            usedConstructFasterVideo |= retryConstructFaster.BonusConfirmed;
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
                        return UpgradeResourceWaitCalculator.BuildBlockedResultMessage(snapshot);
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

                var waitMs = UpgradeResourceWaitCalculator.ComputePostActionWaitMs(durationSeconds);
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
                    await ReloadPageTracedAsync(
                        _page,
                        $"stale build queue attempt {stalePageReloads}/2",
                        new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded },
                        cancellationToken);
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


}
