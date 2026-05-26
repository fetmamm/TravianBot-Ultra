using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<VillageStatus> ReadVillageResourceStatusAsync(CancellationToken cancellationToken = default, bool allowNavigationToResourcePage = true)
    {
        Notify("[ReadVillageResourceStatusAsync] started");
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

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageResourceStatusesAsync(CancellationToken cancellationToken = default)
    {
        Notify("[ReadAllVillageResourceStatusesAsync] started");
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

        Notify($"[ReadAllVillageResourceStatusesAsync] finished count={statuses.Count}");
        return statuses;
    }

    public async Task NavigateToResourceFieldsAsync(CancellationToken cancellationToken = default)
    {
        Notify("[NavigateToResourceFieldsAsync] started");
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
        var constructionNpcTradeAttempted = false;
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

                var highestKnownLevel = await ReadHighestKnownQueuedResourceLevelAsync(resourceName, currentLevel.Value, cancellationToken);
                if (highestKnownLevel >= targetLevel)
                {
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, null, cancellationToken);
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

                var queueFingerprintBefore = BuildQueueFingerprint(snapshot.BuildQueue);
                var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
                var detectedMax = actionability.DetectedMaxLevel;
                var effectiveTarget = detectedMax is int maxLevel ? Math.Min(targetLevel, maxLevel) : targetLevel;
                Notify($"Resource slot {slotId}: level={currentLevel}, target={effectiveTarget}, max={detectedMax}, outcome={actionability.Outcome}.");

                if (currentLevel >= effectiveTarget)
                {
                    return $"Resource slot {slotId} is level {currentLevel}. Target {effectiveTarget} reached after {upgrades} upgrades.";
                }

                if (highestKnownLevel >= effectiveTarget)
                {
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, null, cancellationToken);
                    return $"Resource slot {slotId}: queued upgrade toward level {effectiveTarget}. queue_wait_seconds={queuedWaitSeconds}";
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByMaxLevel)
                {
                    var resolvedMax = detectedMax ?? currentLevel.Value;
                    return $"Resource slot {slotId} appears maxed at level {resolvedMax}. No upgrade performed.";
                }

                if (!constructionNpcTradeAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    constructionNpcTradeAttempted = true;
                    if (await TryNpcTradeForConstructionAsync($"Resource slot {slotId} ({resourceName}) upgrade to level {effectiveTarget}", cancellationToken))
                    {
                        continue;
                    }
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                {
                    var resourceWaitSnapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Resource slot {slotId} ({resourceName}) upgrade to level {effectiveTarget}",
                        ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                        cancellationToken);
                    return BuildUpgradeResourceBlockedResultMessage(resourceWaitSnapshot);
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
                    await NotifyCurrentResourceProductionForUiAsync(cancellationToken);
                    transientRetries = 0;
                    continue;
                }

                if (progress.QueuedOrInProgress)
                {
                    var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, expectedWaitSeconds, cancellationToken);
                    if (highestKnownLevel + 1 < effectiveTarget)
                    {
                        transientRetries = 0;
                        continue;
                    }

                    return $"Resource slot {slotId}: queued upgrade toward level {effectiveTarget}. Evidence: {progress.Evidence}. queue_wait_seconds={queuedWaitSeconds}";
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

    internal static int ComputeResourceUpgradeSafetyCap(int targetLevel)
        => Math.Max(10, targetLevel + 8);

    public async Task<string> UpgradeAllResourcesToLevelAsync(int targetLevel, string buildStrategy = "lowest_first", CancellationToken cancellationToken = default)
    {
        var smartStrategy = string.Equals(buildStrategy, "smart", StringComparison.OrdinalIgnoreCase);
        Notify($"[UpgradeAllResourcesToLevelAsync] targetLevel={targetLevel} strategy={(smartStrategy ? "smart" : "lowest_first")} started");
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Target level must be 0 or higher.");
        }

        var upgrades = 0;
        var knownLevelsBySlot = new Dictionary<int, int>();
        var transientRetries = 0;
        int? currentTransientSlot = null;
        var constructionNpcTradeAttempted = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await EnsureResourceFieldsPageAsync(
                    cancellationToken,
                    "Manual verification appeared while reading resource fields.");
                var resourceFields = await ReadResourceFieldsAsync(cancellationToken);
                NotifyResourceLevelIncreases(knownLevelsBySlot, resourceFields);
                knownLevelsBySlot = BuildResourceLevelMap(resourceFields);
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
                            if (TryParseResourceValue(raw) is { } value)
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

                var preflightSlot = candidateRows.FirstOrDefault(field => (field.Level ?? 0) < Math.Min(targetLevel, fallbackMax))?.SlotId
                    ?? candidateRows.FirstOrDefault()?.SlotId
                    ?? 0;
                var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Resource, preflightSlot, upgrades, cancellationToken);
                if (deferMessage is not null)
                {
                    Notify($"[UpgradeAllResourcesToLevelAsync] queue gate deferred before candidate scan. slot={preflightSlot} message={deferMessage}");
                    return deferMessage;
                }

                var attemptedAny = false;
                var blockReasons = new List<string>();
                UpgradeResourceWaitSnapshot? firstResourceBlockSnapshot = null;
                var queuedTowardTargetCount = 0;
                int? shortestQueuedTargetWaitSeconds = null;

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

                    Notify($"[UpgradeAllResourcesToLevelAsync] evaluating slot={slot} name='{resourceName}' level={level} target={targetLevel}.");
                    var actionability = await AnalyzeUpgradeActionabilityAsync(slot, cancellationToken, performClick: false);
                    var cap = actionability.DetectedMaxLevel ?? fallbackMax;
                    var effectiveTarget = Math.Min(targetLevel, cap);
                    Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} actionability={actionability.Outcome} effectiveTarget={effectiveTarget} max={actionability.DetectedMaxLevel?.ToString() ?? "unknown"} candidateIndex={actionability.CandidateIndex?.ToString() ?? "-"} reason={actionability.Reason}");
                    if (level >= effectiveTarget)
                    {
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} level={level} already meets effective target {effectiveTarget}. Skipping.");
                        continue;
                    }

                    var highestKnownLevel = await ReadHighestKnownQueuedResourceLevelAsync(resourceName, level, cancellationToken);
                    if (highestKnownLevel >= effectiveTarget)
                    {
                        var queuedWaitSeconds = await ReadQueuedResourceWaitSecondsAsync(resourceName, null, cancellationToken);
                        queuedTowardTargetCount += 1;
                        shortestQueuedTargetWaitSeconds = shortestQueuedTargetWaitSeconds is int existingWait
                            ? Math.Min(existingWait, queuedWaitSeconds)
                            : queuedWaitSeconds;
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} already queued toward effective target {effectiveTarget} (highest known queued level {highestKnownLevel}).");
                        continue;
                    }

                    if (actionability.Outcome == UpgradeAttemptOutcome.CanUpgrade)
                    {
                        attemptedAny = true;
                        Notify($"[UpgradeAllResourcesToLevelAsync] clicking upgrade for slot={slot} from level={level} toward target={effectiveTarget}.");
                        var queueFingerprintBefore = BuildQueueFingerprint(await ReadBuildQueueAsync(cancellationToken));
                        var rawUpgradeSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken);
                        await ClickDetectedUpgradeCandidateAsync(slot, actionability.CandidateIndex, cancellationToken);
                        await NavigateToResourceFieldsAfterUpgradeClickAsync(cancellationToken);
                        await WaitForPostUpgradeClickPageLoadAsync(cancellationToken);
                        upgrades += 1;
                        var progress = await WaitForResourceLevelAdvanceAsync(
                            slot,
                            level,
                            queueFingerprintBefore,
                            rawUpgradeSeconds,
                            cancellationToken);
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} click result advanced={progress.Advanced} queued={progress.QueuedOrInProgress} evidence={progress.Evidence}.");
                        if (!progress.Advanced && !progress.QueuedOrInProgress)
                        {
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

                    var label = string.IsNullOrWhiteSpace(candidate.Name)
                        ? $"Resource slot {slot} upgrade to level {effectiveTarget}"
                        : $"Resource slot {slot} ({candidate.Name}) upgrade to level {effectiveTarget}";
                    if (!constructionNpcTradeAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
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
                            ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                            cancellationToken);
                        firstResourceBlockSnapshot ??= snapshot;
                        blockReasons.Add($"slot {slot}: {actionability.Outcome} ({actionability.Reason})");
                        Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} blocked by resources. wait={snapshot.WaitSeconds}s reason={snapshot.WaitReason}.");
                        continue;
                    }

                    blockReasons.Add($"slot {slot}: {actionability.Outcome} ({actionability.Reason})");
                    Notify($"[UpgradeAllResourcesToLevelAsync] slot={slot} not actionable. outcome={actionability.Outcome}.");
                }

                if (!attemptedAny)
                {
                    if (queuedTowardTargetCount > 0 && firstResourceBlockSnapshot is null)
                    {
                        var queuedWaitSeconds = Math.Max(1, shortestQueuedTargetWaitSeconds ?? 1);
                        return $"Resource fields already queued toward target level {targetLevel}. Waiting for queued upgrades to finish. queue_wait_seconds={queuedWaitSeconds}";
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

                    if (firstResourceBlockSnapshot is not null)
                    {
                        Notify($"[UpgradeAllResourcesToLevelAsync] no slot was actionable. Returning first resource wait snapshot after scanning all candidates.");
                        return BuildUpgradeResourceBlockedResultMessage(firstResourceBlockSnapshot);
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

    private async Task<VillageStatus> ReadCurrentVillageResourceStatusAsync(CancellationToken cancellationToken, bool allowNavigationToResourcePage)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared before reading resource status.", cancellationToken);
        var villages = await ReadVillagesPreferCacheAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var buildQueue = await ReadBuildQueueAsync(cancellationToken);
        var remaining = ResolveShortestQueueDurationSeconds(buildQueue);
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
        var resourceFields = HasMeaningfulResourceFields(liveResourceFields)
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
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
                  const compact = (value) => clean(value).replace(/\s+/g, '');
                  const parseNumber = (value) => {
                    const text = clean(value);
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
                    wood: readFirstText(['#l1', '#stockBarResource1 .value', '#stockBarResource1']),
                    clay: readFirstText(['#l2', '#stockBarResource2 .value', '#stockBarResource2']),
                    iron: readFirstText(['#l3', '#stockBarResource3 .value', '#stockBarResource3']),
                    crop: readFirstText(['#l4', '#stockBarResource4 .value', '#stockBarResource4']),
                    warehouse: readFirstText(['#stockBarWarehouse', '#stockBarWarehouse .value', '#warehouse', '#warehouse .value']),
                    granary: readFirstText(['#stockBarGranary', '#stockBarGranary .value', '#stockBarSilo', '#stockBarSilo .value', '#granary', '#granary .value', '#silo', '#silo .value']),
                    woodProduction: readProduction('r1'),
                    clayProduction: readProduction('r2'),
                    ironProduction: readProduction('r3'),
                    cropProduction: readProduction('r4'),
                    diagnostics: [
                      `url=${location.pathname}${location.search}`,
                      `ready=${document.readyState}`,
                      `l1=${readFirstText(['#l1', '#stockBarResource1 .value', '#stockBarResource1']) || '-'}`,
                      `warehouseNode=${document.querySelector('#stockBarWarehouse') ? 'yes' : 'no'}`,
                      `granaryNode=${document.querySelector('#stockBarGranary') ? 'yes' : 'no'}`,
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
                Warehouse: TryParseResourceValue(snapshot?.Warehouse),
                Granary: TryParseResourceValue(snapshot?.Granary));

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
                    await WaitForNavigationSettledAsync(cancellationToken);
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
            .Select(key => $"{key[0]}={FormatResourceLogNumber(TryParseResourceValue(resources.TryGetValue(key, out var raw) ? raw : null))}"));
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
        await WaitForNavigationSettledAsync(cancellationToken);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while waiting for resource widgets.", cancellationToken);

            try
            {
                await _page.WaitForFunctionAsync(
                    """
                    () => {
                      const hasResourceValue = !!document.querySelector('#l1, #stockBarResource1 .value, #stockBarResource1');
                      const hasCapacity = !!document.querySelector('#stockBarWarehouse, #stockBarGranary, #stockBarSilo');
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
                    await WaitForNavigationSettledAsync(cancellationToken);
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
                    `warehouse=${document.querySelector('#stockBarWarehouse') ? 'yes' : 'no'}:${text('#stockBarWarehouse')}`,
                    `granary=${document.querySelector('#stockBarGranary') ? 'yes' : 'no'}:${text('#stockBarGranary')}`,
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
                wood: ['#l1', '#stockBarResource1 .value', '#stockBarResource1'],
                clay: ['#l2', '#stockBarResource2 .value', '#stockBarResource2'],
                iron: ['#l3', '#stockBarResource3 .value', '#stockBarResource3'],
                crop: ['#l4', '#stockBarResource4 .value', '#stockBarResource4']
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
        return raw ?? new Dictionary<string, string>();
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
                  const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
                  const parseNumber = (value) => {
                    const text = clean(value);
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
                    await WaitForNavigationSettledAsync(cancellationToken);
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

    internal static long? TryParseResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var parsed) ? parsed : null;
    }

    private static bool HasMeaningfulResourceFields(IReadOnlyList<ResourceField> fields)
        => fields.Any(field => field.Level is not null || !string.Equals(field.FieldType, "unknown", StringComparison.OrdinalIgnoreCase));

    private string BuildVillageResourceCacheKey(string villageName)
    {
        return $"{_account.Name}|{_config.BaseUrl.TrimEnd('/')}|{(string.IsNullOrWhiteSpace(villageName) ? "unknown" : villageName.Trim())}";
    }

    private CachedVillageResourceSnapshot? TryGetCachedVillageResourceSnapshot(string villageName)
    {
        if (string.IsNullOrWhiteSpace(villageName))
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
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return;
        }

        var key = BuildVillageResourceCacheKey(villageName);
        lock (ResourceStatusCacheSync)
        {
            CachedVillageResourceSnapshotsByKey.TryGetValue(key, out var existing);

            var fieldsToStore = HasMeaningfulResourceFields(resourceFields)
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
        await EnsureResourceFieldsPageAsync(
            cancellationToken,
            "Manual verification appeared while returning to resource fields after upgrade click.");
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

    private async Task<int> ReadHighestKnownQueuedResourceLevelAsync(
        string resourceName,
        int currentLevel,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = await ReadActiveConstructionsAsync(cancellationToken);
            var highestQueuedLevel = active
                .Where(item => item.Kind == ConstructionKind.Resource && SameBuildingName(item.Name, resourceName))
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
            var current = TryParseResourceValue(currentRaw) ?? 0;
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

    private sealed record ResourceUpgradeAffordability(long TimeUntilAffordableSeconds, bool HasUnknownWait, long TotalCost);

}
