using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<VillageStatus> ReadVillageResourceStatusAsync(CancellationToken cancellationToken = default, bool allowNavigationToResourcePage = true)
    {
        Notify("[resources:verbose] ReadVillageResourceStatusAsync started");
        if (allowNavigationToResourcePage && !IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening resource fields.", cancellationToken);
        }

        await EnsureLoggedInAsync();
        if (allowNavigationToResourcePage)
        {
            await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        }

        return await ReadCurrentVillageResourceStatusAsync(cancellationToken, allowNavigationToResourcePage);
    }

    public async Task<VillageStatus> ReadCurrentPageStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("[resources:verbose] ReadCurrentPageStorageStatusAsync started");
        await EnsureLoggedInAsync();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading storage status.", cancellationToken);

        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var snapshot = await ReadResourceSnapshotAsync(
            cancellationToken,
            allowRecovery: false,
            maxAttempts: 1);
        var resources = snapshot.Resources;
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = BuildResourceForecasts(resources, capacities, productionByHour);

        SaveCachedVillageResourceSnapshot(
            activeVillage,
            cachedSnapshot?.ResourceFields ?? [],
            capacities,
            productionByHour);

        Notify($"Storage read: village='{activeVillage}', storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}");

        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var activeConstructions = await ReadActiveConstructionsAsync(
            cancellationToken,
            allowNavigationToBuildings: false);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: [],
            Resources: resources,
            ResourceFields: cachedSnapshot?.ResourceFields ?? [],
            Buildings: [],
            BuildQueue: buildQueue,
            Tribe: string.Empty,
            VillageCount: 0,
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview);
    }

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageResourceStatusesAsync(CancellationToken cancellationToken = default)
    {
        Notify("[resources] all-village resource scan starting");
        await EnsureLoggedInAsync();

        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            return [await ReadVillageResourceStatusAsync(cancellationToken)];
        }

        var statuses = new List<VillageStatus>(villages.Count);
        foreach (var village in villages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SwitchToVillageAsync(village.Name, village.Url, cancellationToken, skipFeatureRefresh: true);
            statuses.Add(await ReadVillageResourceStatusAsync(cancellationToken));
        }

        Notify($"[resources] all-village resource scan finished — {statuses.Count} village(s)");
        return statuses;
    }

    public async Task NavigateToResourceFieldsAsync(CancellationToken cancellationToken = default)
    {
        Notify("[resources:verbose] NavigateToResourceFieldsAsync started");
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync();
    }

    public async Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionPerHourAsync(CancellationToken cancellationToken = default)
    {
        Notify("[ReadCurrentPageResourceProductionPerHourAsync] started");
        await EnsureLoggedInAsync();
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            Notify("ReadCurrentPageResourceProductionPerHourAsync: current page is not dorf1, navigating to resource fields first.");
            await GotoAsync(Paths.Resources, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening resource fields for production read.", cancellationToken);
            await EnsureLoggedInAsync();
        }

        await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        var production = await ReadResourceProductionPerHourAsync(cancellationToken);
        Notify($"ReadCurrentPageResourceProductionPerHourAsync: prod {BuildProductionValueLog(production)}");
        return production;
    }

    public async Task<PageHtmlCapture> ReadCurrentPageHtmlAsync(CancellationToken cancellationToken = default)
    {
        Notify("[ReadCurrentPageHtmlAsync] started");
        cancellationToken.ThrowIfCancellationRequested();
        var url = _page.Url;
        var html = await _page.ContentAsync();
        Notify($"ReadCurrentPageHtmlAsync: captured {html.Length} chars from url='{url}'.");
        return new PageHtmlCapture(url ?? string.Empty, html ?? string.Empty);
    }

    public async Task<PageHtmlCapture> NavigateToPageAndReadHtmlAsync(string pagePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePagePath(pagePath);
        Notify($"[NavigateToPageAndReadHtmlAsync] opening {normalizedPath}");
        await EnsureLoggedInAsync();
        await GotoAsync(normalizedPath, cancellationToken);
        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while opening {normalizedPath}.", cancellationToken);
        return await ReadCurrentPageHtmlAsync(cancellationToken);
    }

    private static string NormalizePagePath(string pagePath)
    {
        var value = (pagePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Page path is empty.");
        }

        return value;
    }

    private async Task NotifyCurrentResourceProductionForUiAsync(CancellationToken cancellationToken)
    {
        try
        {
            Notify("Resource production update: start");
            var production = await ReadCurrentPageResourceProductionPerHourAsync(cancellationToken);
            if (production.Count == 0 || production.Values.All(value => value is null))
            {
                Notify("Resource production update: skipped because no production values were read.");
                return;
            }

            var wood = production.TryGetValue("wood", out var woodValue) ? woodValue : null;
            var clay = production.TryGetValue("clay", out var clayValue) ? clayValue : null;
            var iron = production.TryGetValue("iron", out var ironValue) ? ironValue : null;
            var crop = production.TryGetValue("crop", out var cropValue) ? cropValue : null;
            Notify(
                $"Resource production update: wood={FormatProductionUpdateValue(wood)} clay={FormatProductionUpdateValue(clay)} iron={FormatProductionUpdateValue(iron)} crop={FormatProductionUpdateValue(crop)}");
        }
        catch (Exception ex)
        {
            Notify($"Resource production update: failed ({ex.Message})");
        }
    }

    private static string FormatProductionUpdateValue(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "-";
        }

        return Math.Round(value.Value, MidpointRounding.AwayFromZero)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string> UpgradeResourceToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default)
    {
        using var navDiagnostics = BeginConstructionNavigationDiagnostics($"upgrade_resource_to_level slot={slotId} target={targetLevel}");
        Notify("[resources] upgrade resource field starting");
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
        var safetyCap = UpgradeMath.ComputeResourceUpgradeSafetyCap(targetLevel);
        int? lastKnownLevel = null;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttemptedOffers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                var highestKnownLevel = await ReadHighestKnownQueuedResourceLevelAsync(slotId, resourceName, currentLevel.Value, cancellationToken);
                if (highestKnownLevel >= targetLevel)
                {
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, slotId, null, cancellationToken);
                    return $"Resource slot {slotId}: queued upgrade toward level {targetLevel}. queue_wait_seconds={queuedWaitSeconds}";
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

                var queueFingerprintBefore = BuildQueueFingerprints.Identity(snapshot.BuildQueue);
                var actionability = await AnalyzeUpgradeActionabilityAsync(
                    slotId,
                    cancellationToken,
                    performClick: false,
                    skipNavigationIfOnExpectedSlot: true);
                var detectedMax = actionability.DetectedMaxLevel;
                var effectiveTarget = detectedMax is int maxLevel ? Math.Min(targetLevel, maxLevel) : targetLevel;
                Notify($"Resource slot {slotId}: level={currentLevel}, target={effectiveTarget}, max={detectedMax}, outcome={actionability.Outcome}.");

                if (currentLevel >= effectiveTarget)
                {
                    return $"Resource slot {slotId} is level {currentLevel}. Target {effectiveTarget} reached after {upgrades} upgrades.";
                }

                if (highestKnownLevel >= effectiveTarget)
                {
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, slotId, null, cancellationToken);
                    return $"Resource slot {slotId}: queued upgrade toward level {effectiveTarget}. queue_wait_seconds={queuedWaitSeconds}";
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByMaxLevel)
                {
                    var resolvedMax = detectedMax ?? currentLevel.Value;
                    return $"Resource slot {slotId} appears maxed at level {resolvedMax}. No upgrade performed.";
                }

                var blockedByResources = actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources;
                var pageLooksBlockedByResources = blockedByResources || await CurrentPageLooksBlockedByResourcesAsync(cancellationToken);
                if (pageLooksBlockedByResources)
                {
                    var offerLevel = ResolveResourceUpgradeOfferLevel(currentLevel.Value, effectiveTarget, highestKnownLevel, actionability);
                    var offerCost = await TryReadLiveResourceUpgradeCostOnCurrentPageAsync(cancellationToken);
                    var offerKey = BuildConstructionHeroTransferOfferKey(slotId, offerLevel, offerCost);
                    if (heroTransferAttemptedOffers.Add(offerKey))
                    {
                        var label = $"Resource slot {slotId} ({resourceName}) upgrade to level {offerLevel ?? effectiveTarget}";
                        Notify($"[resources] hero-transfer offer key={offerKey} label='{label}'.");
                        if (await TryHeroResourceTransferForConstructionAsync(label, cancellationToken))
                        {
                            continue;
                        }
                    }
                }
                if (!constructionNpcTradeAttempted && pageLooksBlockedByResources)
                {
                    constructionNpcTradeAttempted = true;
                    var offerLevel = ResolveResourceUpgradeOfferLevel(currentLevel.Value, effectiveTarget, highestKnownLevel, actionability);
                    if (await TryNpcTradeForConstructionAsync($"Resource slot {slotId} ({resourceName}) upgrade to level {offerLevel ?? effectiveTarget}", cancellationToken))
                    {
                        continue;
                    }
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                {
                    var resourceWaitSnapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Resource slot {slotId} ({resourceName}) upgrade to level {effectiveTarget}",
                        UpgradeMath.ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                        cancellationToken);
                    return BuildUpgradeResourceBlockedResultMessage(resourceWaitSnapshot);
                }

                if (actionability.Outcome != UpgradeAttemptOutcome.CanUpgrade)
                {
                    return $"Resource slot {slotId} blocked ({actionability.Outcome}): {actionability.Reason}";
                }

                var pageAnalysis = await ReadConstructionPageAnalysisAsync(
                    slotId,
                    "resource upgrade pre-click",
                    cancellationToken);
                var expectedWaitSeconds = pageAnalysis.DurationSeconds;
                // Read the population this level grants before clicking (page changes after).
                var populationDelta = pageAnalysis.PopulationDelta;
                await ClickDetectedUpgradeCandidateAsync(slotId, actionability.CandidateIndex, cancellationToken);
                await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                upgrades += 1;
                if (populationDelta is int popDelta)
                {
                    await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
                }
                var progress = await WaitForResourceLevelAdvanceAsync(
                    slotId,
                    currentLevel.Value,
                    queueFingerprintBefore,
                    expectedWaitSeconds,
                    cancellationToken);
                if (progress.Advanced)
                {
                    await NotifyCurrentResourceProductionForUiAsync(cancellationToken);
                    transientRetries = 0;
                    continue;
                }

                if (progress.QueuedOrInProgress)
                {
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, slotId, expectedWaitSeconds, cancellationToken);
                    if (highestKnownLevel + 1 < effectiveTarget)
                    {
                        transientRetries = 0;
                        continue;
                    }

                    return $"Resource slot {slotId}: queued upgrade toward level {effectiveTarget}. Evidence: {progress.Evidence}. queue_wait_seconds={queuedWaitSeconds}";
                }

                var queueDeferMessage = await CheckQueueOrDeferAsync(
                    ConstructionKind.Resource,
                    slotId,
                    upgrades,
                    cancellationToken,
                    allowNavigationToBuildings: false);
                if (queueDeferMessage is not null)
                {
                    return queueDeferMessage;
                }

                return $"Resource slot {slotId} blocked (NoImmediateProgress): Upgrade triggered but level is still {currentLevel} and no queue/in-progress evidence was detected queue_wait_seconds=6";
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex) && transientRetries < 6)
            {
                transientRetries += 1;
                Notify($"UpgradeResourceToLevelAsync hit transient navigation context at slot {slotId} ({transientRetries}/6). Retrying...");
                await Task.Delay(250 * transientRetries, cancellationToken);
            }
        }

        var levelText = lastKnownLevel is int level ? level.ToString() : "unknown";
        return $"Resource slot {slotId}: hit safety cap of {safetyCap} iterations while targeting level {targetLevel}. Upgrades performed: {upgrades}. Last known level: {levelText}.";
    }

    public async Task<string> UpgradeAllResourcesToLevelAsync(int targetLevel, string buildStrategy = "lowest_first", CancellationToken cancellationToken = default)
    {
        using var navDiagnostics = BeginConstructionNavigationDiagnostics($"upgrade_all_resources_to_level target={targetLevel}");
        var smartStrategy = string.Equals(buildStrategy, "smart", StringComparison.OrdinalIgnoreCase);
        Notify($"[UpgradeAllResourcesToLevelAsync] targetLevel={targetLevel} strategy={(smartStrategy ? "smart" : "lowest_first")} started");
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Target level must be 0 or higher.");
        }

        var upgrades = 0;
        var transientRetries = 0;
        int? currentTransientSlot = null;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttemptedOffers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await EnsureResourceFieldsPageAsync(
                    cancellationToken,
                    "Manual verification appeared while reading resource fields.");
                var resourceFields = await ReadResourceFieldsAsync(cancellationToken);
                // Note: each successful upgrade is already announced by WaitForResourceLevelAdvanceAsync
                // at the moment the level advances. We deliberately do NOT diff levels again here, as
                // that produced a duplicate "Resource slot N level increased ..." line per upgrade.
                var fallbackMax = 40;
                var actionableFields = resourceFields
                    .Where(field => field.SlotId is not null && field.Level is not null);

                List<ResourceField> candidateRows;
                Dictionary<string, long>? stockByType = null;
                if (smartStrategy)
                {
                    try
                    {
                        var snapshot = await ReadResourceSnapshotAsync(cancellationToken);
                        var parsed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        foreach (var resourceKey in new[] { "wood", "clay", "iron", "crop" })
                        {
                            var raw = snapshot.Resources.TryGetValue(resourceKey, out var rawValue) ? rawValue : null;
                            if (TravianParsing.TryParseResourceValue(raw) is { } value)
                            {
                                parsed[resourceKey] = value;
                            }
                        }

                        // Only use smart ordering when at least one stock value was readable.
                        if (parsed.Count > 0)
                        {
                            stockByType = parsed;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Notify($"[UpgradeAllResourcesToLevelAsync] smart strategy could not read storage ({ex.Message}). Falling back to lowest-level-first.");
                    }
                }

                if (stockByType is not null)
                {
                    candidateRows = actionableFields
                        .OrderBy(field => stockByType.TryGetValue(field.FieldType, out var stock) ? stock : long.MaxValue)
                        .ThenBy(field => field.Level ?? 0)
                        .ThenBy(field => field.SlotId ?? 999)
                        .ToList();
                    string Stock(string key) => stockByType.TryGetValue(key, out var v) ? v.ToString() : "?";
                    var orderNote = stockByType.Values.Distinct().Count() <= 1
                        ? "tracked stocks equal; lowest-level-first tiebreak"
                        : "ordered by lowest stock";
                    Notify($"[UpgradeAllResourcesToLevelAsync] smart {orderNote}. wood={Stock("wood")} clay={Stock("clay")} iron={Stock("iron")} crop={Stock("crop")}.");
                }
                else
                {
                    candidateRows = actionableFields
                        .OrderBy(field => field.Level ?? 0)
                        .ThenBy(field => field.SlotId ?? 999)
                        .ToList();
                }

                Notify($"[UpgradeAllResourcesToLevelAsync] scanned {candidateRows.Count} resource fields on dorf1.");
                await NotifyCurrentResourceProductionForUiAsync(cancellationToken);

                var attemptedAny = false;
                var anyQueuedTowardTarget = false;
                var blockReasons = new List<string>();
                foreach (var candidate in candidateRows)
                {
                    var slot = candidate.SlotId ?? 0;
                    currentTransientSlot = slot;
                    var level = candidate.Level ?? 0;
                    var preliminaryTarget = Math.Min(targetLevel, fallbackMax);
                    var resourceName = string.IsNullOrWhiteSpace(candidate.Name)
                        ? $"slot {slot}"
                        : candidate.Name;

                    if (level >= preliminaryTarget)
                    {
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} level={level} already meets preliminary target {preliminaryTarget}. Skipping.");
                        continue;
                    }

                    // Over-build guard (matches the single-slot UpgradeResourceToLevelAsync): if this resource
                    // slot already has a queued/in-progress upgrade that reaches the target, do NOT click again.
                    // The build page of a slot with a pending upgrade offers the NEXT level (target+1), so a
                    // second click would overshoot the requested target. This fires on Plus/Roman accounts where
                    // a second queue slot is free while one upgrade is already in flight. If Travian does not
                    // expose a slot id for the queued resource, fall back to same-name matching conservatively.
                    var highestQueuedLevel = await ReadHighestKnownQueuedResourceLevelAsync(slot, resourceName, level, cancellationToken);
                    if (highestQueuedLevel >= preliminaryTarget)
                    {
                        anyQueuedTowardTarget = true;
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} '{resourceName}' already has a queued upgrade reaching level {highestQueuedLevel} (target {preliminaryTarget}). Skipping to avoid over-building.");
                        continue;
                    }

                    var preflightQueueDeferMessage = await CheckQueueOrDeferAsync(
                        ConstructionKind.Resource,
                        slot,
                        upgrades,
                        cancellationToken,
                        allowNavigationToBuildings: false);
                    if (preflightQueueDeferMessage is not null)
                    {
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} deferred by dorf1 queue gate before opening upgrade page. message={preflightQueueDeferMessage}");
                        return preflightQueueDeferMessage;
                    }

                ReevaluateCurrentSlot:
                    Notify($"[UpgradeAllResourcesToLevelAsync] evaluating slot={slot} name='{resourceName}' level={level} target={targetLevel}.");
                    var actionability = await AnalyzeUpgradeActionabilityAsync(
                        slot,
                        cancellationToken,
                        performClick: false,
                        skipNavigationIfOnExpectedSlot: true);
                    var cap = actionability.DetectedMaxLevel ?? fallbackMax;
                    var effectiveTarget = Math.Min(targetLevel, cap);
                    Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} actionability={actionability.Outcome} effectiveTarget={effectiveTarget} detectedTarget={actionability.DetectedTargetLevel?.ToString() ?? "unknown"} max={actionability.DetectedMaxLevel?.ToString() ?? "unknown"} candidateIndex={actionability.CandidateIndex?.ToString() ?? "-"} reason={actionability.Reason}");
                    if (level >= effectiveTarget)
                    {
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} level={level} already meets effective target {effectiveTarget}. Skipping.");
                        continue;
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.CanUpgrade)
                    {
                        attemptedAny = true;
                        Notify($"[UpgradeAllResourcesToLevelAsync] clicking upgrade for slot={slot} from level={level} toward target={effectiveTarget}.");
                        var queueFingerprintBefore = BuildQueueFingerprints.Identity(await ReadBuildQueueAsync(cancellationToken));
                        var pageAnalysis = await ReadConstructionPageAnalysisAsync(
                            slot,
                            "resource bulk upgrade pre-click",
                            cancellationToken);
                        var rawUpgradeSeconds = pageAnalysis.DurationSeconds;
                        // Read the population this level grants before clicking (page changes after).
                        var populationDelta = pageAnalysis.PopulationDelta;
                        await ClickDetectedUpgradeCandidateAsync(slot, actionability.CandidateIndex, cancellationToken);
                        await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                        upgrades += 1;
                        if (populationDelta is int popDelta)
                        {
                            await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
                        }
                        var progress = await WaitForResourceLevelAdvanceAsync(
                            slot,
                            level,
                            queueFingerprintBefore,
                            rawUpgradeSeconds,
                            cancellationToken);
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} click result advanced={progress.Advanced} queued={progress.QueuedOrInProgress} evidence={progress.Evidence}.");
                        if (!progress.Advanced && !progress.QueuedOrInProgress)
                        {
                            var queueDeferMessage = await CheckQueueOrDeferAsync(
                                ConstructionKind.Resource,
                                slot,
                                upgrades,
                                cancellationToken,
                                allowNavigationToBuildings: false);
                            if (queueDeferMessage is not null)
                            {
                                Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} deferred by dorf1 queue gate after unconfirmed click. message={queueDeferMessage}");
                                return queueDeferMessage;
                            }

                            var upgradeWaitSeconds = ComputeResourceUpgradeWaitSeconds(rawUpgradeSeconds);
                            Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} click did not confirm immediately ({progress.Evidence}). Deferring {upgradeWaitSeconds}s before retry.");
                            return $"Resource slot {slot}: upgrade click did not confirm immediately ({progress.Evidence}). queue_wait_seconds={Math.Max(1, upgradeWaitSeconds)}";
                        }
                        transientRetries = 0;
                        goto NextLoopTick;
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByQueue)
                    {
                        var queueDeferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Resource, slot, upgrades, cancellationToken);
                        if (queueDeferMessage is not null)
                        {
                            Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} deferred by queue gate after analysis. message={queueDeferMessage}");
                            return queueDeferMessage;
                        }

                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} blocked by queue. Retrying after queue clears.");
                        transientRetries = 0;
                        goto NextLoopTick;
                    }

                    var offerLevel = ResolveResourceUpgradeOfferLevel(level, effectiveTarget, highestQueuedLevel, actionability);
                    var label = string.IsNullOrWhiteSpace(candidate.Name)
                        ? $"Resource slot {slot} upgrade to level {offerLevel ?? effectiveTarget}"
                        : $"Resource slot {slot} ({candidate.Name}) upgrade to level {offerLevel ?? effectiveTarget}";
                    var blockedByResources = actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources;
                    var pageLooksBlockedByResources = blockedByResources || await CurrentPageLooksBlockedByResourcesAsync(cancellationToken);
                    if (pageLooksBlockedByResources)
                    {
                        var offerCost = await TryReadLiveResourceUpgradeCostOnCurrentPageAsync(cancellationToken);
                        var offerKey = BuildConstructionHeroTransferOfferKey(slot, offerLevel, offerCost);
                        if (heroTransferAttemptedOffers.Add(offerKey))
                        {
                            Notify($"[UpgradeAllResourcesToLevelAsync] hero-transfer offer key={offerKey} label='{label}'.");
                            if (await TryHeroResourceTransferForConstructionAsync(label, cancellationToken))
                            {
                                Notify($"[UpgradeAllResourcesToLevelAsync] hero transfer completed for slot={slot}; rechecking same build page before navigating away.");
                                goto ReevaluateCurrentSlot;
                            }
                        }
                    }
                    if (!constructionNpcTradeAttempted && pageLooksBlockedByResources)
                    {
                        constructionNpcTradeAttempted = true;
                        if (await TryNpcTradeForConstructionAsync(label, cancellationToken))
                        {
                            goto NextLoopTick;
                        }
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                    {
                        var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                            label,
                            UpgradeMath.ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                            cancellationToken);
                        blockReasons.Add($"slot {slot}: {actionability.Outcome} ({actionability.Reason})");
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} blocked by resources. Deferring construction for {snapshot.WaitSeconds}s instead of scanning more slots. reason={snapshot.WaitReason}.");
                        return BuildUpgradeResourceBlockedResultMessage(snapshot);
                    }

                    blockReasons.Add($"slot {slot}: {actionability.Outcome} ({actionability.Reason})");
                    Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} not actionable. outcome={actionability.Outcome}.");
                }

                if (!attemptedAny)
                {
                    if (blockReasons.Count == 0 && anyQueuedTowardTarget)
                    {
                        // Nothing left to click, but at least one field still has a queued upgrade reaching the
                        // target. Defer (re-check after it finishes) instead of declaring "all done" prematurely.
                        var queuedWait = await ReadQueuedResourceWaitSecondsAsync(string.Empty, null, null, cancellationToken);
                        return $"Resource fields: queued upgrade(s) already reaching target level {targetLevel}. Upgrades made: {upgrades}. queue_wait_seconds={queuedWait}";
                    }

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
                    Notify($"Upgrade-all hit transient navigation context at slot {slotId} ({transientRetries}/8). Retrying...");
                }
                else
                {
                    Notify($"Upgrade-all hit transient navigation context ({transientRetries}/8). Retrying...");
                }
                await Task.Delay(300 * transientRetries, cancellationToken);
            }
        }
    }

    private async Task<VillageStatus> ReadCurrentVillageResourceStatusAsync(CancellationToken cancellationToken, bool allowNavigationToResourcePage)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading resource status.", cancellationToken);
        var villages = await ReadVillagesPreferCacheAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        // Read the hero adventure indicator from the current page (cheap, no navigation) so the
        // periodic refresh keeps the dashboard/hero adventure count in sync with the live game.
        var heroSidebar = await ReadHeroSidebarStatusAsync(cancellationToken);
        int? adventureCount = heroSidebar.AdventureFound ? Math.Max(0, heroSidebar.AdventureCount) : null;
        // Read Travian's own in-progress construction list from the current page so the buildings /
        // resources UI keeps showing upgrades started outside the program (target level in parentheses).
        var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings: false);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var activeBuildCount = ConstructionSlots.ActiveBuildCount(buildQueue, activeConstructions);
        if (buildQueue.Count != activeConstructions.Count)
        {
            Notify(
                $"[construction-status:verbose] active count sources differ " +
                $"village='{activeVillage}' buildQueue={buildQueue.Count} " +
                $"activeConstructions={activeConstructions.Count} selected={activeBuildCount}");
        }
        var remaining = TravianParsing.ResolveShortestQueueDurationSeconds(buildQueue);
        var currency = await ReadCurrencyAsync(cancellationToken);
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        if (allowNavigationToResourcePage)
        {
            await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
        }

        var snapshot = await ReadResourceSnapshotAsync(
            cancellationToken,
            allowRecovery: allowNavigationToResourcePage,
            maxAttempts: allowNavigationToResourcePage ? 4 : 1);
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(activeVillage);
        var resources = snapshot.Resources;
        var capacities = (
            Warehouse: snapshot.Capacities.Warehouse ?? cachedSnapshot?.WarehouseCapacity,
            Granary: snapshot.Capacities.Granary ?? cachedSnapshot?.GranaryCapacity);
        var productionByHour = MergeProductionByHour(snapshot.ProductionByHour, cachedSnapshot?.ProductionByHour);
        var forecasts = BuildResourceForecasts(resources, capacities, productionByHour);
        var usingCachedProduction = !HasAnyProduction(snapshot.ProductionByHour) && HasAnyProduction(cachedSnapshot?.ProductionByHour);
        Notify($"Resource read: storage wh={FormatResourceLogNumber(capacities.Warehouse)} gr={FormatResourceLogNumber(capacities.Granary)} | stock {BuildResourceValueLog(resources)} | prod {BuildProductionValueLog(productionByHour)}{(usingCachedProduction ? " (cached production)" : string.Empty)}");

        var liveResourceFields = await ReadResourceFieldsAsync(cancellationToken);
        var liveResourceFieldsComplete = HasCompleteResourceFieldSnapshot(liveResourceFields);
        if (!liveResourceFieldsComplete)
        {
            Notify(
                $"[resources:verbose] partial resource field snapshot ignored for cache " +
                $"village='{activeVillage}' fields={liveResourceFields.Count} " +
                $"knownLevels={liveResourceFields.Count(field => field.Level is >= 0)}.");
        }

        var resourceFields = liveResourceFieldsComplete
            ? liveResourceFields
            : cachedSnapshot?.ResourceFields ?? liveResourceFields;

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

        SaveCachedVillageResourceSnapshot(
            activeVillage,
            resourceFields,
            capacities,
            productionByHour);

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
            IsBuildingInProgress: activeBuildCount > 0,
            ActiveBuildCount: activeBuildCount,
            BuildQueueRemainingSeconds: remaining,
            BuildQueueRemainingText: remaining is int left ? TravianParsing.FormatDuration(left) : string.Empty,
            IsCapital: cachedIsCapital,
            ServerTimeUtc: _serverTimeUtc,
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports,
            WarehouseCapacity: capacities.Warehouse,
            GranaryCapacity: capacities.Granary,
            ResourceStorageForecasts: forecasts,
            AdventureCount: adventureCount,
            ActiveConstructions: activeConstructions,
            BuildQueueFinish: remaining is > 0 ? TimerSnapshot.FromRemaining(remaining.Value) : null,
            ActiveConstructionsFromOverview: _lastActiveConstructionsFromOverview);
    }

    private async Task<(
        IReadOnlyDictionary<string, string> Resources,
        (long? Warehouse, long? Granary) Capacities,
        IReadOnlyDictionary<string, double?> ProductionByHour)> ReadResourceSnapshotAsync(
            CancellationToken cancellationToken,
            bool allowRecovery = true,
            int maxAttempts = 4)
    {
        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource snapshot.", cancellationToken);

            var snapshot = await _page.EvaluateAsync<ResourceSnapshotDomReadResult>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/[\u202A-\u202E\u2066-\u2069]/g, '').replace(/\s+/g, ' ').trim();
                  const compact = (value) => clean(value).replace(/\s+/g, '');
                  const parseNumber = (value) => {
                    const text = clean(value).replace(/[\u2212\u2012\u2013\u2014]/g, '-');
                    if (!text) return null;
                    const match = text.match(/([+-]?\d[\d\s.,']*)/);
                    if (!match) return null;
                    const normalized = match[1].replace(/\s+/g, '').replace(/,/g, '').replace(/'/g, '');
                    const parsed = Number(normalized);
                    return Number.isFinite(parsed) ? parsed : null;
                  };

                  const readFirstText = (selectors) => {
                    for (const selector of selectors) {
                      const node = document.querySelector(selector);
                      if (!node) continue;
                      const values = [
                        node.getAttribute?.('data-value'),
                        node.getAttribute?.('data-amount'),
                        node.getAttribute?.('data-max'),
                        node.getAttribute?.('data-capacity'),
                        node.innerText,
                        node.textContent,
                        node.querySelector?.('.value')?.innerText,
                        node.querySelector?.('.value')?.textContent
                      ];
                      for (const value of values) {
                        const text = compact(value);
                        if (text) return text;
                      }
                    }
                    return null;
                  };

                  const readProduction = (resourceClass) => {
                    const row = document.querySelector(`#production i.${resourceClass}`)?.closest('tr');
                    if (!row) return null;
                    const valueCell = row.querySelector('td.num');
                    return parseNumber(valueCell?.innerText || valueCell?.textContent || row.innerText || row.textContent || '');
                  };

                  return {
                    wood: readFirstText(['#l1']),
                    clay: readFirstText(['#l2']),
                    iron: readFirstText(['#l3']),
                    crop: readFirstText(['#l4']),
                    warehouse: readFirstText(['#warehouse', '#warehouse .value', '.warehouse .capacity .value', '.warehouse .value']),
                    granary: readFirstText(['#granary', '#granary .value', '#silo', '#silo .value', '.granary .capacity .value', '.granary .value']),
                    woodProduction: readProduction('r1'),
                    clayProduction: readProduction('r2'),
                    ironProduction: readProduction('r3'),
                    cropProduction: readProduction('r4'),
                    diagnostics: [
                      `url=${location.pathname}${location.search}`,
                      `ready=${document.readyState}`,
                      `l1=${readFirstText(['#l1']) || '-'}`,
                      `warehouseNode=${document.querySelector('.warehouse .capacity .value, #warehouse') ? 'yes' : 'no'}`,
                      `granaryNode=${document.querySelector('.granary .capacity .value, #granary, #silo') ? 'yes' : 'no'}`,
                      `productionNode=${document.querySelector('#production') ? 'yes' : 'no'}`,
                      `productionText=${String(readProduction('r1') ?? '-')}`,
                      `bodyClass=${document.body?.className || '-'}`
                    ].join(', ')
                  };
                }
                """);

            var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddResourceIfPresent(resources, "wood", snapshot?.Wood);
            AddResourceIfPresent(resources, "clay", snapshot?.Clay);
            AddResourceIfPresent(resources, "iron", snapshot?.Iron);
            AddResourceIfPresent(resources, "crop", snapshot?.Crop);

            var productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = snapshot?.WoodProduction,
                ["clay"] = snapshot?.ClayProduction,
                ["iron"] = snapshot?.IronProduction,
                ["crop"] = snapshot?.CropProduction,
            };
            var capacities = (
                Warehouse: TravianParsing.TryParseResourceValue(snapshot?.Warehouse),
                Granary: TravianParsing.TryParseResourceValue(snapshot?.Granary));

            var hasResources = resources.Count > 0;
            var hasCapacity = capacities.Warehouse is not null || capacities.Granary is not null;
            var hasProduction = productionByHour.Values.Any(value => value is not null);
            if (hasResources || hasCapacity || hasProduction)
            {
                if (attempt > 1)
                {
                    Notify($"Resource read recovered on attempt {attempt}/4: {snapshot?.Diagnostics ?? "-"}");
                }

                return (resources, capacities, productionByHour);
            }

            Notify($"Resource read incomplete {attempt}/{attempts}: {snapshot?.Diagnostics ?? "-"}");
            if (!allowRecovery)
            {
                continue;
            }

            if (attempt == 2)
            {
                try
                {
                    await _page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _config.TimeoutMs,
                    }).WaitAsync(cancellationToken);
                    await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                    await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    Notify($"Resource snapshot reload hit transient navigation context: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    Notify("Resource snapshot reload timed out while waiting for DOMContentLoaded.");
                }
            }

            await Task.Delay(250 * attempt, cancellationToken);
        }

        return (
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            (null, null),
            new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasAnyProduction(IReadOnlyDictionary<string, double?>? productionByHour)
        => productionByHour is not null && productionByHour.Values.Any(value => value is not null);

    private static IReadOnlyDictionary<string, double?> MergeProductionByHour(
        IReadOnlyDictionary<string, double?> live,
        IReadOnlyDictionary<string, double?>? cached)
    {
        var merged = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = null,
            ["clay"] = null,
            ["iron"] = null,
            ["crop"] = null,
        };

        foreach (var key in merged.Keys.ToList())
        {
            live.TryGetValue(key, out var liveValue);
            if (liveValue is not null)
            {
                merged[key] = liveValue;
                continue;
            }

            if (cached is not null && cached.TryGetValue(key, out var cachedValue))
            {
                merged[key] = cachedValue;
            }
        }

        return merged;
    }

    private static void AddResourceIfPresent(IDictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static string BuildResourceValueLog(IReadOnlyDictionary<string, string> resources)
    {
        return string.Join(" ", new[] { "wood", "clay", "iron", "crop" }
            .Select(key => $"{key[0]}={FormatResourceLogNumber(TravianParsing.TryParseResourceValue(resources.TryGetValue(key, out var raw) ? raw : null))}"));
    }

    private static string BuildProductionValueLog(IReadOnlyDictionary<string, double?> productionByHour)
    {
        return string.Join(" ", new[] { "wood", "clay", "iron", "crop" }
            .Select(key =>
            {
                productionByHour.TryGetValue(key, out var value);
                return $"{key[0]}={FormatResourceLogNumber(value)}";
            }));
    }

    private static string FormatResourceLogNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
    }

    private static string FormatResourceLogNumber(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "-";
        }

        return Math.Round(value.Value, MidpointRounding.AwayFromZero)
            .ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", " ");
    }

    private async Task WaitForResourceSnapshotWidgetsAsync(CancellationToken cancellationToken)
    {
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while waiting for resource widgets.", cancellationToken);

            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const hasResourceValue = !!document.querySelector('#l1');
                      const hasCapacity = !!document.querySelector('.warehouse .capacity .value, .granary .capacity .value');
                      const hasProduction = !!document.querySelector('#production td.num, #production tbody tr');
                      return hasResourceValue && hasCapacity && hasProduction;
                    }
                    """,
                    new PageWaitForFunctionOptions
                    {
                        Timeout = 1500,
                    }).WaitAsync(cancellationToken);

                if (attempt > 1)
                {
                    Notify($"Resource widgets became available on attempt {attempt}.");
                }

                return;
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            var diagnostics = await ReadResourceSnapshotDiagnosticsAsync(cancellationToken);
            Notify($"Resource widget wait attempt {attempt}/4: {diagnostics}");

            if (attempt >= 4)
            {
                return;
            }

            if (attempt == 2)
            {
                try
                {
                    await _page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _config.TimeoutMs,
                    }).WaitAsync(cancellationToken);
                    await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                    continue;
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    Notify($"Resource widget reload hit transient navigation context: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    Notify("Resource widget reload timed out while waiting for DOMContentLoaded.");
                }
            }

            await Task.Delay(300 * attempt, cancellationToken);
        }
    }

    private async Task<string> ReadResourceSnapshotDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource diagnostics.", cancellationToken);

        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const text = (selector) => {
                    const node = document.querySelector(selector);
                    if (!node) return '-';
                    return (node.textContent || '').replace(/\s+/g, ' ').trim() || '(empty)';
                  };

                  return [
                    `url=${location.pathname}${location.search}`,
                    `ready=${document.readyState}`,
                    `l1=${document.querySelector('#l1') ? 'yes' : 'no'}:${text('#l1')}`,
                    `warehouse=${document.querySelector('.warehouse .capacity .value') ? 'yes' : 'no'}:${text('.warehouse .capacity .value')}`,
                    `granary=${document.querySelector('.granary .capacity .value') ? 'yes' : 'no'}:${text('.granary .capacity .value')}`,
                    `production=${document.querySelector('#production') ? 'yes' : 'no'}`,
                    `villageMap=${document.querySelector('#village_map') ? 'yes' : 'no'}`,
                    `bodyClass=${document.body?.className || '-'}`
                  ].join(', ');
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return $"diagnostics unavailable: {ex.Message}";
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadResourcesAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resources.", cancellationToken);
        var raw = await _page.EvaluateAsync<Dictionary<string, string>>(
            """
            () => {
              const readValue = (selector) => {
                const element = document.querySelector(selector);
                if (!element) return null;
                const candidates = [
                  element.getAttribute('data-value'),
                  element.getAttribute('data-amount'),
                  element.getAttribute('aria-label'),
                  element.getAttribute('title'),
                  element.textContent,
                  element.querySelector?.('.value')?.textContent || ''
                ];
                for (const candidate of candidates) {
                  const value = String(candidate || '').replace(/\s+/g, '').trim();
                  if (value) return value;
                }
                return null;
              };

              const ids = {
                wood: ['#l1'],
                clay: ['#l2'],
                iron: ['#l3'],
                crop: ['#l4']
              };
              const resources = {};

              for (const [name, selectors] of Object.entries(ids)) {
                for (const selector of selectors) {
                  const value = readValue(selector);
                  if (value) {
                    resources[name] = value;
                    break;
                  }
                }
              }

              return resources;
            }
            """);
        var result = raw ?? new Dictionary<string, string>();
        if (result.Count == 0)
        {
            Notify("[resources:verbose] ReadResourcesAsync read 0 values (page may not be dorf1/dorf2 or stock bar not loaded)");
        }
        else
        {
            Notify($"[resources:verbose] ReadResourcesAsync — wood={result.GetValueOrDefault("wood", "-")} clay={result.GetValueOrDefault("clay", "-")} iron={result.GetValueOrDefault("iron", "-")} crop={result.GetValueOrDefault("crop", "-")}");
        }
        return result;
    }

    private sealed class ResourceSnapshotDomReadResult
    {
        public string? Wood { get; set; }
        public string? Clay { get; set; }
        public string? Iron { get; set; }
        public string? Crop { get; set; }
        public string? Warehouse { get; set; }
        public string? Granary { get; set; }
        public double? WoodProduction { get; set; }
        public double? ClayProduction { get; set; }
        public double? IronProduction { get; set; }
        public double? CropProduction { get; set; }
        public string? Diagnostics { get; set; }
    }

    private async Task<IReadOnlyDictionary<string, double?>> ReadResourceProductionPerHourAsync(CancellationToken cancellationToken)
    {
        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading production rates.", cancellationToken);

            var snapshot = await _page.EvaluateAsync<ResourceSnapshotDomReadResult>(
                """
                () => {
                  const clean = (value) => String(value || '').replace(/[\u202A-\u202E\u2066-\u2069]/g, '').replace(/\s+/g, ' ').trim();
                  const parseNumber = (value) => {
                    const text = clean(value).replace(/[\u2212\u2012\u2013\u2014]/g, '-');
                    if (!text) return null;
                    const match = text.match(/([+-]?\d[\d\s.,']*)/);
                    if (!match) return null;
                    const normalized = match[1].replace(/\s+/g, '').replace(/,/g, '').replace(/'/g, '');
                    const parsed = Number(normalized);
                    return Number.isFinite(parsed) ? parsed : null;
                  };

                  const readProduction = (resourceClass) => {
                    const row = document.querySelector(`#production i.${resourceClass}`)?.closest('tr');
                    if (!row) return null;
                    const valueCell = row.querySelector('td.num');
                    return parseNumber(valueCell?.innerText || valueCell?.textContent || row.innerText || row.textContent || '');
                  };

                  return {
                    woodProduction: readProduction('r1'),
                    clayProduction: readProduction('r2'),
                    ironProduction: readProduction('r3'),
                    cropProduction: readProduction('r4'),
                    diagnostics: [
                      `url=${location.pathname}${location.search}`,
                      `ready=${document.readyState}`,
                      `productionNode=${document.querySelector('#production') ? 'yes' : 'no'}`,
                      `productionText=${String(readProduction('r1') ?? '-')}`,
                      `bodyClass=${document.body?.className || '-'}`
                    ].join(', ')
                  };
                }
                """);

            var productionByHour = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = snapshot?.WoodProduction,
                ["clay"] = snapshot?.ClayProduction,
                ["iron"] = snapshot?.IronProduction,
                ["crop"] = snapshot?.CropProduction,
            };

            if (productionByHour.Values.Any(value => value is not null))
            {
                if (attempt > 1)
                {
                    Notify($"Production read recovered on attempt {attempt}/{attempts}: {snapshot?.Diagnostics ?? "-"}");
                }

                return productionByHour;
            }

            Notify($"Production read incomplete {attempt}/{attempts}: {snapshot?.Diagnostics ?? "-"}");

            if (attempt == 2)
            {
                try
                {
                    await _page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _config.TimeoutMs,
                    }).WaitAsync(cancellationToken);
                    await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                    await WaitForResourceSnapshotWidgetsAsync(cancellationToken);
                }
                catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
                {
                    Notify($"Production read reload hit transient navigation context: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    Notify("Production read reload timed out while waiting for DOMContentLoaded.");
                }
            }

            await Task.Delay(250 * attempt, cancellationToken);
        }

        return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ResourceStorageForecast> BuildResourceForecasts(
        IReadOnlyDictionary<string, string> resources,
        (long? Warehouse, long? Granary) capacities,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        var result = new List<ResourceStorageForecast>();
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            resources.TryGetValue(key, out var rawCurrent);
            var current = TravianParsing.TryParseResourceValue(rawCurrent);
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
                var remaining = Math.Max(0L, capacity.Value - current.Value);
                var computedSeconds = Math.Ceiling((remaining / production.Value) * 3600.0);
                secondsToFull = computedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)computedSeconds;
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

    private static bool HasCompleteResourceFieldSnapshot(IReadOnlyList<ResourceField> fields)
    {
        var bySlot = fields
            .Where(field => field.SlotId is >= 1 and <= 18)
            .GroupBy(field => field.SlotId!.Value)
            .ToList();
        if (bySlot.Count != 18)
        {
            return false;
        }

        return bySlot.All(group =>
        {
            var field = group.First();
            return field.Level is >= 0
                && IsKnownResourceFieldType(field.FieldType);
        });
    }

    private static bool IsKnownResourceFieldType(string? fieldType)
        => string.Equals(fieldType, "wood", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldType, "clay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldType, "iron", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldType, "crop", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownVillageName(string villageName)
        => !string.IsNullOrWhiteSpace(villageName)
            && !string.Equals(villageName.Trim(), "Unknown village", StringComparison.OrdinalIgnoreCase);

    private string BuildVillageResourceCacheKey(string villageName)
    {
        return $"{_account.Name}|{_config.BaseUrl.TrimEnd('/')}|{(string.IsNullOrWhiteSpace(villageName) ? "unknown" : villageName.Trim())}";
    }

    private CachedVillageResourceSnapshot? TryGetCachedVillageResourceSnapshot(string villageName)
    {
        if (!IsKnownVillageName(villageName))
        {
            return null;
        }

        var key = BuildVillageResourceCacheKey(villageName);
        lock (ResourceStatusCacheSync)
        {
            return CachedVillageResourceSnapshotsByKey.TryGetValue(key, out var snapshot)
                ? snapshot
                : null;
        }
    }

    private void SaveCachedVillageResourceSnapshot(
        string villageName,
        IReadOnlyList<ResourceField> resourceFields,
        (long? Warehouse, long? Granary) capacities,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        if (!IsKnownVillageName(villageName))
        {
            return;
        }

        var key = BuildVillageResourceCacheKey(villageName);
        lock (ResourceStatusCacheSync)
        {
            CachedVillageResourceSnapshotsByKey.TryGetValue(key, out var existing);

            var fieldsToStore = HasCompleteResourceFieldSnapshot(resourceFields)
                ? resourceFields.Select(field => field with { }).ToList()
                : existing?.ResourceFields ?? [];

            var productionToStore = HasAnyProduction(productionByHour)
                ? new Dictionary<string, double?>(productionByHour, StringComparer.OrdinalIgnoreCase)
                : existing?.ProductionByHour ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

            var warehouse = capacities.Warehouse ?? existing?.WarehouseCapacity;
            var granary = capacities.Granary ?? existing?.GranaryCapacity;

            if (fieldsToStore.Count == 0
                && productionToStore.Count == 0
                && warehouse is null
                && granary is null)
            {
                return;
            }

            CachedVillageResourceSnapshotsByKey[key] = new CachedVillageResourceSnapshot
            {
                ResourceFields = fieldsToStore,
                ProductionByHour = productionToStore,
                WarehouseCapacity = warehouse,
                GranaryCapacity = granary,
            };
        }
    }

    private async Task<IReadOnlyList<ResourceField>> ReadResourceFieldsAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource fields.", cancellationToken);
        await WaitForResourceFieldsHydratedAsync(cancellationToken);

        // Primary scan: read the image map (#rx) area links and the #village_map level
        // overlays directly. Modern Travian (2026) uses hrefs like dorf1.php?<cyrillic-a>=<slot>
        // instead of the legacy build.php?id=<slot>; the legacy selectors miss everything.
        string primaryJson = string.Empty;
        try
        {
            primaryJson = await _page.EvaluateAsync<string>(
                """
                () => {
                  // Slot parameter key varies across Travian skins/servers:
                  //   - Modern obfuscated: dorf1.php?а=N (Cyrillic 'а', U+0430)
                  //   - Latin variant:     dorf1.php?a=N
                  //   - Legacy:            build.php?id=N
                  // Match all three so the scan works regardless of which markup the
                  // server emits today.
                  const slotKeyPattern = /[?&](?:id|a|а)=(\d{1,2})(?:[^0-9]|$)/i;
                  const fieldTypes = { 1: 'wood', 2: 'clay', 3: 'iron', 4: 'crop' };
                  const fieldNames = {
                    wood: 'Woodcutter',
                    clay: 'Clay pit',
                    iron: 'Iron mine',
                    crop: 'Cropland'
                  };

                  // 1) Collect slot anchors from the image map, preserving HTML order
                  //    (Travian emits areas in slot-id 1..18 order).
                  const areas = Array.from(document.querySelectorAll('map#rx area, map[name="rx"] area'));
                  const slots = [];
                  for (const area of areas) {
                    const href = area.getAttribute('href') || '';
                    const m = href.match(slotKeyPattern);
                    if (!m) continue;
                    const slotId = parseInt(m[1], 10);
                    if (slotId < 1 || slotId > 18) continue;
                    const coords = (area.getAttribute('coords') || '').split(',').map(s => parseFloat(s.trim()));
                    slots.push({
                      slotId,
                      cx: coords[0],
                      cy: coords[1],
                      href
                    });
                  }

                  // 2) Collect level overlays from #village_map. Each has gid<N> and level<N>
                  //    classes plus a left/top inline style. Travian emits overlays in the
                  //    same order as areas — slot 1 first, slot 18 last — so we can pair
                  //    them by index without trusting the offset positions (which are
                  //    sometimes 0 before CSS finishes applying).
                  const overlays = Array.from(document.querySelectorAll('#village_map .level'));
                  const overlayInfo = overlays.map(el => {
                    const cls = el.className || '';
                    const gidMatch = cls.match(/\bgid(\d+)\b/i);
                    const levelMatch = cls.match(/\blevel(\d+)\b/i);
                    const labelText = ((el.querySelector('.labelLayer') || {}).textContent || '').trim();
                    const labelLevel = /^\d+$/.test(labelText) ? parseInt(labelText, 10) : null;
                    const style = el.getAttribute('style') || '';
                    const leftMatch = style.match(/left\s*:\s*(-?\d+(?:\.\d+)?)px/i);
                    const topMatch = style.match(/top\s*:\s*(-?\d+(?:\.\d+)?)px/i);
                    return {
                      gid: gidMatch ? parseInt(gidMatch[1], 10) : null,
                      level: labelLevel ?? (levelMatch ? parseInt(levelMatch[1], 10) : null),
                      left: leftMatch ? parseFloat(leftMatch[1]) : NaN,
                      top: topMatch ? parseFloat(topMatch[1]) : NaN
                    };
                  });

                  // 3a) Preferred path: zip by index when both lists have the same length.
                  //     This is the common case (always 18+18) and avoids spatial mismatches
                  //     caused by offsetWidth==0 during initial render.
                  const out = [];
                  if (slots.length === overlays.length && slots.length > 0) {
                    for (let i = 0; i < slots.length; i++) {
                      const slot = slots[i];
                      const overlay = overlayInfo[i];
                      const fieldType = overlay && fieldTypes[overlay.gid] ? fieldTypes[overlay.gid] : 'unknown';
                      out.push({
                        slotId: slot.slotId,
                        fieldType,
                        name: fieldNames[fieldType] || 'Unknown field',
                        level: overlay ? overlay.level : null,
                        href: slot.href
                      });
                    }
                  } else {
                    // 3b) Fallback: spatial matching when counts disagree. Pair each slot
                    //     to its nearest overlay using inline-style left/top, with a tolerance
                    //     generous enough to absorb the icon→label offset (~40-60px).
                    const used = new Set();
                    for (const slot of slots) {
                      let bestIdx = -1;
                      let bestDist = Infinity;
                      for (let i = 0; i < overlayInfo.length; i++) {
                        if (used.has(i)) continue;
                        const ov = overlayInfo[i];
                        if (!isFinite(ov.left) || !isFinite(ov.top)) continue;
                        // Overlays are placed top-left, ~40px right and ~10px above the icon
                        // centre. Compare overlay top-left to area centre directly.
                        const dx = ov.left - slot.cx;
                        const dy = ov.top - slot.cy;
                        const dist = Math.sqrt(dx * dx + dy * dy);
                        if (dist < bestDist) {
                          bestDist = dist;
                          bestIdx = i;
                        }
                      }
                      const overlay = (bestIdx >= 0 && bestDist <= 120) ? overlayInfo[bestIdx] : null;
                      if (overlay) used.add(bestIdx);

                      const fieldType = overlay && fieldTypes[overlay.gid] ? fieldTypes[overlay.gid] : 'unknown';
                      out.push({
                        slotId: slot.slotId,
                        fieldType,
                        name: fieldNames[fieldType] || 'Unknown field',
                        level: overlay ? overlay.level : null,
                        href: slot.href
                      });
                    }
                  }

                  // Diagnostic for empty results so we can keep up with Travian markup drift.
                  if (out.length === 0) {
                    try {
                      window.__resourceFieldScanDiag = {
                        url: location.pathname + location.search,
                        areaCount: areas.length,
                        slotsParsed: slots.length,
                        overlayCount: overlays.length,
                        sampleArea: areas.length > 0 ? (areas[0].getAttribute('href') || '') : '',
                        sampleOverlay: overlays.length > 0 ? (overlays[0].className || '') : ''
                      };
                    } catch (_) {}
                  }

                  return JSON.stringify(out);
                }
                """);
        }
        catch (Exception ex) when (IsTransientExecutionContextException(ex))
        {
            Notify($"Resource field primary scan hit transient navigation context ({ex.Message}). Falling back to legacy scan.");
        }

        // If the primary map-based scan succeeded, return immediately.
        if (!string.IsNullOrWhiteSpace(primaryJson))
        {
            var primary = JsonSerializer.Deserialize<List<ResourceFieldJs>>(primaryJson) ?? new List<ResourceFieldJs>();
            if (primary.Count > 0)
            {
                return BuildResourceFieldsFromJs(primary);
            }
        }

        // Legacy scan kept as a safety net for older skins / pre-map Travian variants.
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
                'a[href*="build.php?id="]',
                // Modern Travian skins drop the <area>/<a> map and bind clicks via JS,
                // leaving only the resource-level overlays + onclick handlers. Pick them
                // up via the `aid<N>` class on overlay/container divs.
                '#village_map [class*="aid"]',
                '#resourceFieldContainer [class*="aid"]',
                '#rx [class*="aid"]',
                '#village_map [class*="gid"]',
                '#resourceFieldContainer [class*="gid"]',
                '#rx [class*="gid"]',
                '[onclick*="build.php?id="]',
                '[data-href*="build.php?id="]'
              ];

              const seen = new Set();
              const fields = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href')
                    || element.getAttribute('data-href')
                    || element.getAttribute('onclick')
                    || '';
                  const slotId = parseSlotId(element, href);
                  if (slotId === null || slotId < 1 || slotId > 18) continue;
                  const key = String(slotId);
                  if (seen.has(key)) continue;
                  seen.add(key);
                  const fieldType = parseType(element, slotId);
                  fields.push({
                    slotId,
                    fieldType,
                    name: parseName(fieldType, element),
                    level: parseLevel(element, slotId),
                    href: href || `build.php?id=${slotId}`
                  });
                }
                // Stop once we have all 18 fields to keep selector order priority.
                if (fields.length >= 18) break;
              }

              // Always log diagnostic info to the page console so playwright can pipe it
              // back when scans come up empty. Helps catch Travian markup changes early.
              if (fields.length === 0) {
                try {
                  const diag = {
                    url: location.pathname + location.search,
                    areaWithBuild: document.querySelectorAll('area[href*="build.php"]').length,
                    anchorWithBuild: document.querySelectorAll('a[href*="build.php"]').length,
                    villageMapAids: document.querySelectorAll('#village_map [class*="aid"]').length,
                    villageMapGids: document.querySelectorAll('#village_map [class*="gid"]').length,
                    resourceFieldContainer: !!document.querySelector('#resourceFieldContainer'),
                    rx: !!document.querySelector('#rx'),
                    villageMap: !!document.querySelector('#village_map'),
                    levelOverlays: document.querySelectorAll('#village_map .level').length,
                    onclickWithBuild: document.querySelectorAll('[onclick*="build.php"]').length
                  };
                  // Stash the diagnostic on window so the C# follow-up read can grab it.
                  window.__resourceFieldScanDiag = diag;
                } catch (_) {}
              }

              return JSON.stringify(fields);
            }
            """);
        }, cancellationToken: cancellationToken);

        var rawFields = string.IsNullOrWhiteSpace(rawFieldsJson)
            ? new List<ResourceFieldJs>()
            : JsonSerializer.Deserialize<List<ResourceFieldJs>>(rawFieldsJson) ?? new List<ResourceFieldJs>();

        rawFields ??= [];

        // If the dorf1 scan picked up zero field links, try a broader probe before
        // falling back to placeholders. Travian sometimes leaves the image-map <area>
        // elements out of the initial DOM when navigating between dorf1/dorf2, and on
        // some skins they're replaced entirely by onclick/data-attribute bindings on
        // overlay divs (aid<N>/gid<N> classes).
        if (rawFields.Count == 0 && IsCurrentUrlForPath(Paths.Resources))
        {
            // Read the diagnostic stashed by the main scan so we can see exactly which
            // markup variant is present (or absent) on this page.
            try
            {
                var diag = await _page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.__resourceFieldScanDiag || null)");
                if (!string.IsNullOrWhiteSpace(diag) && !string.Equals(diag, "null", StringComparison.Ordinal))
                {
                    Notify($"Resource field scan diagnostic: {diag}");
                }
            }
            catch
            {
                // Diagnostic read is best-effort.
            }

            Notify("Resource field scan returned 0 link elements. Reloading dorf1 once and retrying.");
            try
            {
                await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await WaitForResourceFieldsHydratedAsync(cancellationToken);

                await RetryAsync("read resource fields snapshot (retry)", async () =>
                {
                    rawFieldsJson = await _page.EvaluateAsync<string>(
                        """
                        () => {
                          const parseSlotIdFromText = (value) => {
                            if (!value) return null;
                            const idMatch = String(value).match(/[?&]id=(\d+)/i);
                            if (idMatch) return Number(idMatch[1]);
                            const aidMatch = String(value).match(/(?:^|[^a-z])aid[_:=\s-]?(\d{1,2})/i);
                            if (aidMatch) return Number(aidMatch[1]);
                            const slotMatch = String(value).match(/(?:^|[^a-z])slot[_:=\s-]?(\d{1,2})/i);
                            if (slotMatch) return Number(slotMatch[1]);
                            return null;
                          };
                          const collectSlot = (element) => {
                            if (!element) return null;
                            const candidates = [
                              element.getAttribute && element.getAttribute('href'),
                              element.getAttribute && element.getAttribute('data-href'),
                              element.getAttribute && element.getAttribute('onclick'),
                              element.getAttribute && element.getAttribute('data-aid'),
                              element.getAttribute && element.getAttribute('data-slot'),
                              element.className || '',
                              element.id || ''
                            ];
                            for (const c of candidates) {
                              const s = parseSlotIdFromText(c);
                              if (s !== null && s >= 1 && s <= 18) return s;
                            }
                            let parent = element.parentElement;
                            for (let i = 0; parent && i < 3; i++, parent = parent.parentElement) {
                              const s = parseSlotIdFromText((parent.className || '') + ' ' + (parent.getAttribute && parent.getAttribute('href') || ''));
                              if (s !== null && s >= 1 && s <= 18) return s;
                            }
                            return null;
                          };

                          const selectors = [
                            'area[href*="build.php?id="]',
                            'a[href*="build.php?id="]',
                            '#village_map [class*="aid"]',
                            '#village_map [class*="gid"]',
                            '#resourceFieldContainer [class*="aid"]',
                            '#resourceFieldContainer [class*="gid"]',
                            '#rx [class*="aid"]',
                            '#rx [class*="gid"]',
                            '[onclick*="build.php?id="]',
                            '[data-href*="build.php?id="]'
                          ];

                          const out = [];
                          const seen = new Set();
                          for (const sel of selectors) {
                            for (const el of document.querySelectorAll(sel)) {
                              const slotId = collectSlot(el);
                              if (slotId === null || seen.has(slotId)) continue;
                              seen.add(slotId);
                              const href = el.getAttribute('href')
                                || el.getAttribute('data-href')
                                || `build.php?id=${slotId}`;
                              out.push({ slotId, fieldType: 'unknown', name: '', level: null, href });
                            }
                            if (out.length >= 18) break;
                          }
                          return JSON.stringify(out);
                        }
                        """);
                }, cancellationToken: cancellationToken);

                rawFields = string.IsNullOrWhiteSpace(rawFieldsJson)
                    ? new List<ResourceFieldJs>()
                    : JsonSerializer.Deserialize<List<ResourceFieldJs>>(rawFieldsJson) ?? new List<ResourceFieldJs>();
                rawFields ??= [];
                Notify($"Resource field retry scan picked up {rawFields.Count} link element(s).");
            }
            catch (Exception ex) when (IsTransientExecutionContextException(ex))
            {
                Notify($"Resource field reload hit transient navigation context ({ex.Message}). Continuing with placeholders.");
            }
        }

        return BuildResourceFieldsFromJs(rawFields);
    }

    /// <summary>
    /// Common projection used by both the modern (map+overlay) scan and the legacy fallback
    /// scan. Maps raw JS field rows into <see cref="ResourceField"/>s and tops up the result
    /// with placeholder rows so SlotId 1..18 are always present.
    /// </summary>
    private List<ResourceField> BuildResourceFieldsFromJs(List<ResourceFieldJs> rawFields)
    {
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

    /// <summary>
    /// Waits up to ~3s for dorf1's resource field link elements to appear in the DOM.
    /// The buildings overview scan added a similar hydration wait to fix partial reads
    /// on V3 layouts; dorf1 has the same lazy-mount behaviour when the page is reused
    /// across status refreshes (Travian sometimes drops the <area> map briefly during
    /// background ajax refreshes). Without this wait the very next refresh after a
    /// fresh navigation returns 0 fields even though the page is on dorf1.
    /// </summary>
    private async Task WaitForResourceFieldsHydratedAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  // Accept all three slot-link variants Travian uses: build.php?id=N (legacy),
                  // dorf1.php?a=N (latin), dorf1.php?а=N (Cyrillic 'а').
                  const links = document.querySelectorAll('map#rx area, map[name="rx"] area, area[href*="build.php?id="], a[href*="build.php?id="], area[href*="dorf1.php"], a[href*="dorf1.php"]');
                  let count = 0;
                  const seen = new Set();
                  for (const link of links) {
                    const href = link.getAttribute('href') || '';
                    const m = href.match(/[?&](?:id|a|а)=(\d{1,2})(?:[^0-9]|$)/i);
                    if (!m) continue;
                    const slotId = parseInt(m[1], 10);
                    if (slotId < 1 || slotId > 18 || seen.has(slotId)) continue;
                    seen.add(slotId);
                    count++;
                  }
                  return count >= 18;
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }
        catch (TimeoutException)
        {
            // Continue; the retry-with-reload path inside ReadResourceFieldsAsync still
            // recovers if the JS scan comes back empty.
        }
        catch (Exception ex) when (!IsTransientExecutionContextException(ex))
        {
            Notify($"Resource field hydration wait skipped: {ex.Message}");
        }
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

    private async Task EnsureResourceFieldsPageAsync(CancellationToken cancellationToken, string manualVerificationMessage)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            for (var i = 0; i < 8 && !IsCurrentUrlForPath(Paths.Resources); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(125, cancellationToken);
            }
        }

        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await PauseForManualStepIfVisibleAsync(manualVerificationMessage, cancellationToken);
        await EnsureLoggedInAsync();
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

            if (i == 0 && HasResourceQueueProgress(queueFingerprintBefore, latestSnapshot))
            {
                return new UpgradeProgressResult(false, true, "queue changed");
            }
        }

        if (latestSnapshot is null)
        {
            latestSnapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
        }

        var queueFingerprintAfter = BuildQueueFingerprints.Identity(latestSnapshot.BuildQueue);
        if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
        {
            return new UpgradeProgressResult(false, true, "queue changed");
        }

        var waitSeconds = ComputeResourceUpgradeWaitSeconds(expectedWaitSeconds);
        if (waitSeconds > 0)
        {
            return new UpgradeProgressResult(false, false, $"no immediate queue evidence; expected_wait_seconds={waitSeconds}");
        }

        return new UpgradeProgressResult(false, false, "no queue or level change");
    }

    private static bool HasResourceQueueProgress(string queueFingerprintBefore, ResourceProgressSnapshot snapshot)
    {
        var queueFingerprintAfter = BuildQueueFingerprints.Identity(snapshot.BuildQueue);
        return !string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal);
    }

    private async Task<ResourceProgressSnapshot> ReadResourceProgressSnapshotAsync(CancellationToken cancellationToken)
    {
        await EnsureResourceFieldsPageAsync(
            cancellationToken,
            "Manual verification appeared while reading resource progress.");
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
        int? slotId,
        int? fallbackSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = await ReadActiveConstructionsAsync(cancellationToken);
            var resourceConstructions = active
                .Where(item => item.Kind == ConstructionKind.Resource)
                .ToList();
            var resourceTimers = resourceConstructions
                .Where(item => item.TimeLeftSeconds is int seconds && seconds > 0)
                .ToList();
            // Also treat the page as stale when Travian's own .timer.no-reload marker is present:
            // that marker means the in-page auto-reload failed, so even if some timer values look
            // plausible the rest of the DOM may not reflect the current construction state.
            var hadStaleResourceTimer = (resourceConstructions.Count > 0 && resourceTimers.Count == 0)
                || await IsPageMarkedStaleAsync();
            if (hadStaleResourceTimer)
            {
                Notify("Resource queue timer is zero or stale; reloading resource fields before deferring.");
                await ReloadOrGotoAsync(Paths.Resources, cancellationToken);
                active = await ReadActiveConstructionsAsync(cancellationToken);
                resourceTimers = active
                    .Where(item => item.Kind == ConstructionKind.Resource && item.TimeLeftSeconds is int seconds && seconds > 0)
                    .ToList();
            }

            if (resourceTimers.Count == 0)
            {
                return hadStaleResourceTimer ? 1 : Math.Max(1, ComputeResourceUpgradeWaitSeconds(fallbackSeconds));
            }

            var matchingTimers = ResourceConstructionQueueMatcher.MatchForResourceSlot(resourceTimers, slotId, resourceName)
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

    private async Task<int> ReadHighestKnownQueuedResourceLevelAsync(
        int slotId,
        string resourceName,
        int currentLevel,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = await ReadActiveConstructionsAsync(cancellationToken);
            return ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(active, slotId, resourceName, currentLevel);
        }
        catch
        {
            return currentLevel;
        }
    }

    private sealed record ResourceProgressSnapshot(
        IReadOnlyList<ResourceField> ResourceFields,
        IReadOnlyList<BuildQueueItem> BuildQueue);

    private async Task<IReadOnlyList<ResourceUpgradeCandidate>> RankResourceUpgradeCandidatesAsync(
        IReadOnlyList<ResourceField> candidates,
        int targetLevel,
        int fallbackMaxLevel,
        IReadOnlyDictionary<string, string> resources,
        IReadOnlyDictionary<string, double?> productionByHour,
        CancellationToken cancellationToken)
    {
        var ranked = new List<ResourceUpgradeCandidate>();
        foreach (var candidate in candidates)
        {
            var slotId = candidate.SlotId ?? 0;
            var level = candidate.Level ?? 0;
            var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
            var cap = actionability.DetectedMaxLevel ?? fallbackMaxLevel;
            var effectiveTarget = Math.Min(targetLevel, cap);
            if (level >= effectiveTarget || actionability.Outcome == UpgradeAttemptOutcome.BlockedByMaxLevel)
            {
                continue;
            }

            var nextLevel = level + 1;
            var cost = await TryReadLiveResourceUpgradeCostOnCurrentPageAsync(cancellationToken)
                ?? TryReadCatalogResourceUpgradeCost(candidate, nextLevel);
            if (cost is null)
            {
                ranked.Add(new ResourceUpgradeCandidate(candidate, actionability, long.MaxValue, true, long.MaxValue, false));
                continue;
            }

            var evaluation = EvaluateResourceUpgradeAffordability(cost, resources, productionByHour);
            ranked.Add(new ResourceUpgradeCandidate(
                candidate,
                actionability,
                evaluation.TimeUntilAffordableSeconds,
                evaluation.HasUnknownWait,
                evaluation.TotalCost,
                true));
        }

        if (ranked.Count == 0)
        {
            return candidates.Select(candidate => new ResourceUpgradeCandidate(
                candidate,
                null,
                long.MaxValue,
                true,
                long.MaxValue,
                false)).ToList();
        }

        if (ranked.All(candidate => !candidate.HasReadableCost))
        {
            return candidates.Select(candidate => new ResourceUpgradeCandidate(
                candidate,
                null,
                long.MaxValue,
                true,
                long.MaxValue,
                false)).ToList();
        }

        return ranked
            .OrderBy(candidate => candidate.HasUnknownWait)
            .ThenBy(candidate => candidate.TimeUntilAffordableSeconds)
            .ThenBy(candidate => candidate.Field.Level ?? 0)
            .ThenBy(candidate => candidate.TotalCost)
            .ThenBy(candidate => candidate.Field.SlotId ?? 999)
            .ToList();
    }

    private async Task<ResourceUpgradeCostSnapshot?> TryReadLiveResourceUpgradeCostOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading resource upgrade costs.", cancellationToken);
        var required = await _page.EvaluateAsync<Dictionary<string, long?>>(
            """
            () => {
              const keys = ['wood', 'clay', 'iron', 'crop'];
              const iconClasses = { wood: 'r1', clay: 'r2', iron: 'r3', crop: 'r4' };
              const parseNumber = (value) => {
                const digits = (value || '').replace(/[^\d-]/g, '');
                if (!digits) return null;
                const parsed = Number(digits);
                return Number.isFinite(parsed) ? parsed : null;
              };

              const readCost = (key) => {
                const iconClass = iconClasses[key];
                const containers = [
                  ...document.querySelectorAll('.upgradeBuilding, .contract, .contractWrapper, .build_details, #contract, form[action*="build.php"], .inlineIconList')
                ];

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

                return null;
              };

              const result = {};
              for (const key of keys) {
                result[key] = readCost(key);
              }

              return result;
            }
            """);

        if (!required.TryGetValue("wood", out var wood)
            || !required.TryGetValue("clay", out var clay)
            || !required.TryGetValue("iron", out var iron)
            || !required.TryGetValue("crop", out var crop))
        {
            return null;
        }

        if (wood is null || clay is null || iron is null || crop is null)
        {
            return null;
        }

        return new ResourceUpgradeCostSnapshot(wood.Value, clay.Value, iron.Value, crop.Value);
    }

    private static ResourceUpgradeCostSnapshot? TryReadCatalogResourceUpgradeCost(ResourceField candidate, int nextLevel)
    {
        var gid = ResolveResourceFieldGid(candidate);
        if (gid is null)
        {
            return null;
        }

        var stats = BuildingCatalogService.CostFor(gid.Value, nextLevel);
        return stats is null
            ? null
            : new ResourceUpgradeCostSnapshot(stats.Wood, stats.Clay, stats.Iron, stats.Crop);
    }

    private static int? ResolveResourceFieldGid(ResourceField field)
    {
        var raw = $"{field.FieldType} {field.Name}".ToLowerInvariant();
        if (raw.Contains("wood"))
        {
            return 1;
        }

        if (raw.Contains("clay"))
        {
            return 2;
        }

        if (raw.Contains("iron"))
        {
            return 3;
        }

        if (raw.Contains("crop") || raw.Contains("grain"))
        {
            return 4;
        }

        return null;
    }

    private static ResourceUpgradeAffordability EvaluateResourceUpgradeAffordability(
        ResourceUpgradeCostSnapshot cost,
        IReadOnlyDictionary<string, string> resources,
        IReadOnlyDictionary<string, double?> productionByHour)
    {
        long longest = 0;
        var hasUnknownWait = false;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            var required = key switch
            {
                "wood" => cost.Wood,
                "clay" => cost.Clay,
                "iron" => cost.Iron,
                _ => cost.Crop,
            };

            resources.TryGetValue(key, out var currentRaw);
            var current = TravianParsing.TryParseResourceValue(currentRaw) ?? 0;
            var missing = Math.Max(0, required - current);
            if (missing <= 0)
            {
                continue;
            }

            productionByHour.TryGetValue(key, out var production);
            if (production is > 0)
            {
                var wait = (long)Math.Ceiling((missing / production.Value) * 3600d);
                longest = Math.Max(longest, Math.Max(1L, wait));
                continue;
            }

            hasUnknownWait = true;
        }

        return new ResourceUpgradeAffordability(
            hasUnknownWait ? long.MaxValue : longest,
            hasUnknownWait,
            cost.Wood + cost.Clay + cost.Iron + cost.Crop);
    }

    private sealed record ResourceUpgradeCandidate(
        ResourceField Field,
        UpgradeAttemptResult? Actionability,
        long TimeUntilAffordableSeconds,
        bool HasUnknownWait,
        long TotalCost,
        bool HasReadableCost);

    private sealed record ResourceUpgradeCostSnapshot(long Wood, long Clay, long Iron, long Crop);

    internal static string BuildConstructionHeroTransferOfferKeyForTests(
        int slotId,
        int? detectedTargetLevel,
        long? wood,
        long? clay,
        long? iron,
        long? crop)
    {
        var cost = wood is long w && clay is long c && iron is long i && crop is long cr
            ? new ResourceUpgradeCostSnapshot(w, c, i, cr)
            : null;
        return BuildConstructionHeroTransferOfferKey(slotId, detectedTargetLevel, cost);
    }

    private static string BuildConstructionHeroTransferOfferKey(int slotId, int? detectedTargetLevel, ResourceUpgradeCostSnapshot? cost)
    {
        var levelPart = detectedTargetLevel is int level ? $"level:{level}" : "level:unknown";
        var costPart = cost is null
            ? "cost:unknown"
            : $"cost:{cost.Wood}:{cost.Clay}:{cost.Iron}:{cost.Crop}";
        return $"slot:{slotId}|{levelPart}|{costPart}";
    }

    private static int? ResolveResourceUpgradeOfferLevel(
        int currentLevel,
        int effectiveTarget,
        int highestKnownQueuedLevel,
        UpgradeAttemptResult actionability)
    {
        if (actionability.DetectedTargetLevel is int detectedTargetLevel)
        {
            return detectedTargetLevel;
        }

        var nextKnownLevel = highestKnownQueuedLevel > currentLevel
            ? highestKnownQueuedLevel + 1
            : currentLevel + 1;

        return Math.Min(effectiveTarget, nextKnownLevel);
    }

    private sealed record ResourceUpgradeAffordability(long TimeUntilAffordableSeconds, bool HasUnknownWait, long TotalCost);

}
