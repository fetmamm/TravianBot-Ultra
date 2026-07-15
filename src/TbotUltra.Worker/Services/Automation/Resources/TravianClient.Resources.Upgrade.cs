using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Resource upgrade orchestration, progress verification and candidate ranking.
public sealed partial class TravianClient
{
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

                // Decide the humanized start time while the resource overview and its construction
                // snapshot are still current. Opening build.php before this decision produces an
                // unnatural build-page -> overview -> build-page round trip on a deferred start.
                var humanizeDefer = await MaybeGetConstructionHumanizeDeferAsync(
                    ConstructionKind.Resource,
                    slotId,
                    cancellationToken,
                    allowNavigationToBuildings: false);
                if (humanizeDefer is not null)
                {
                    return humanizeDefer;
                }

                var buildQueueBefore = snapshot.BuildQueue;
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
                    return UpgradeResourceWaitCalculator.BuildBlockedResultMessage(resourceWaitSnapshot);
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
                var constructFaster = await TryUseConstructFasterForResourceAsync(
                    slotId,
                    resourceName,
                    currentLevel.Value,
                    Math.Min(effectiveTarget, highestKnownLevel + 1),
                    buildQueueBefore,
                    expectedWaitSeconds,
                    cancellationToken);
                var usedConstructFasterVideo = constructFaster.BonusConfirmed;
                if (!constructFaster.ActionRegistered)
                {
                    await ClickDetectedUpgradeCandidateAsync(slotId, actionability.CandidateIndex, cancellationToken);
                    await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                }
                await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                upgrades += 1;
                Notify($"[resources] {(usedConstructFasterVideo ? "25% faster video ok" : "click ok")} — slot={slotId} '{resourceName}' lvl {currentLevel} → {Math.Min(effectiveTarget, highestKnownLevel + 1)} queued (duration~{expectedWaitSeconds}s).");
                if (populationDelta is int popDelta)
                {
                    await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
                }
                var progress = await WaitForResourceLevelAdvanceAsync(
                    slotId,
                    currentLevel.Value,
                    resourceName,
                    Math.Min(effectiveTarget, highestKnownLevel + 1),
                    buildQueueBefore,
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
                var buildQueueAtScan = await ReadBuildQueueAsync(cancellationToken);
                // Note: each successful upgrade is already announced by WaitForResourceLevelAdvanceAsync
                // at the moment the level advances. We deliberately do NOT diff levels again here, as
                // that produced a duplicate "Resource slot N level increased ..." line per upgrade.
                var fallbackMax = 40;
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
                    candidateRows = ResourceSnapshotCalculator.OrderUpgradeCandidates(resourceFields, stockByType).ToList();
                    string Stock(string key) => stockByType.TryGetValue(key, out var v) ? v.ToString() : "?";
                    var orderNote = stockByType.Values.Distinct().Count() <= 1
                        ? "tracked stocks equal; lowest-level-first tiebreak"
                        : "ordered by lowest stock";
                    Notify($"[UpgradeAllResourcesToLevelAsync] smart {orderNote}. wood={Stock("wood")} clay={Stock("clay")} iron={Stock("iron")} crop={Stock("crop")}.");
                }
                else
                {
                    candidateRows = ResourceSnapshotCalculator.OrderUpgradeCandidates(resourceFields, stockByType: null).ToList();
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
                        // Already at target — the per-slot skip line was pure per-tick noise; the final
                        // "All resource fields are at or above target" return already summarizes this.
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

                    // Keep the delay decision on dorf1. If a delay is due, never visit the upgrade
                    // page until the retry that can actually click it.
                    var humanizeDefer = await MaybeGetConstructionHumanizeDeferAsync(
                        ConstructionKind.Resource,
                        slot,
                        cancellationToken,
                        allowNavigationToBuildings: false);
                    if (humanizeDefer is not null)
                    {
                        return humanizeDefer;
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
                        continue;
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.CanUpgrade)
                    {
                        attemptedAny = true;
                        Notify($"[UpgradeAllResourcesToLevelAsync] clicking upgrade for slot={slot} from level={level} toward target={effectiveTarget}.");
                        var pageAnalysis = await ReadConstructionPageAnalysisAsync(
                            slot,
                            "resource bulk upgrade pre-click",
                            cancellationToken);
                        var rawUpgradeSeconds = pageAnalysis.DurationSeconds;
                        // Read the population this level grants before clicking (page changes after).
                        var populationDelta = pageAnalysis.PopulationDelta;
                        var nextLevel = Math.Min(effectiveTarget, highestQueuedLevel + 1);
                        var constructFaster = await TryUseConstructFasterForResourceAsync(
                            slot,
                            resourceName,
                            level,
                            nextLevel,
                            buildQueueAtScan,
                            rawUpgradeSeconds,
                            cancellationToken);
                        var usedConstructFasterVideo = constructFaster.BonusConfirmed;
                        if (!constructFaster.ActionRegistered)
                        {
                            await ClickDetectedUpgradeCandidateAsync(slot, actionability.CandidateIndex, cancellationToken);
                            await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                        }
                        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
                        upgrades += 1;
                        Notify($"[resources] {(usedConstructFasterVideo ? "25% faster video ok" : "click ok")} — slot={slot} '{resourceName}' lvl {level} → {nextLevel} queued (duration~{rawUpgradeSeconds}s).");
                        if (populationDelta is int popDelta)
                        {
                            await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
                        }
                        var progress = await WaitForResourceLevelAdvanceAsync(
                            slot,
                            level,
                            resourceName,
                            nextLevel,
                            buildQueueAtScan,
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
                        return UpgradeResourceWaitCalculator.BuildBlockedResultMessage(snapshot);
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

    private async Task NavigateToResourceFieldsAfterUpgradeClickAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

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

        await EnsureLoggedInAsync();
    }

    private async Task<UpgradeProgressResult> WaitForResourceLevelAdvanceAsync(
        int slotId,
        int previousLevel,
        string resourceName,
        int targetLevel,
        IReadOnlyList<BuildQueueItem> buildQueueBefore,
        int? expectedWaitSeconds,
        CancellationToken cancellationToken)
    {
        var queueFingerprintBefore = BuildQueueFingerprints.Identity(buildQueueBefore);
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

            var targetQueueItem = BuildQueueFingerprints.FindNewTargetBuilding(
                buildQueueBefore,
                latestSnapshot.BuildQueue,
                resourceName,
                slotId,
                gid: null,
                targetLevel);
            if (targetQueueItem is not null)
            {
                return new UpgradeProgressResult(
                    false,
                    true,
                    $"resource queue added slot {slotId} {resourceName} level {targetLevel}");
            }

            if (i == 0 && HasResourceQueueProgress(queueFingerprintBefore, latestSnapshot))
            {
                Notify(
                    $"Resource progress check for slot {slotId}: queue changed but no new "
                    + $"{resourceName} level {targetLevel} entry was found.");
            }
        }

        if (latestSnapshot is null)
        {
            latestSnapshot = await ReadResourceProgressSnapshotAsync(cancellationToken);
        }

        var queueFingerprintAfter = BuildQueueFingerprints.Identity(latestSnapshot.BuildQueue);
        if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
        {
            Notify(
                $"Resource progress check for slot {slotId}: final queue changed but no new "
                + $"{resourceName} level {targetLevel} entry was found.");
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
        => ResourceSnapshotCalculator.ComputeUpgradeWaitSeconds(detectedSeconds);

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
            var activeLevel = ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(
                active,
                slotId,
                resourceName,
                currentLevel);
            if (activeLevel > currentLevel)
            {
                return activeLevel;
            }

            // Some Official queue variants expose the resource reliably in the compact build queue but
            // omit kind/slot metadata from the active-construction parser. Use that same-page identity as
            // a conservative fallback so an already queued level is not offered repeatedly.
            var buildQueue = await ReadBuildQueueAsync(cancellationToken);
            return ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(
                buildQueue,
                slotId,
                resourceName,
                currentLevel);
        }
        catch
        {
            return currentLevel;
        }
    }

    private sealed record ResourceProgressSnapshot(
        IReadOnlyList<ResourceField> ResourceFields,
        IReadOnlyList<BuildQueueItem> BuildQueue);

    private async Task<ResourceUpgradeCostSnapshot?> TryReadLiveResourceUpgradeCostOnCurrentPageAsync(CancellationToken cancellationToken)
    {
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

}
