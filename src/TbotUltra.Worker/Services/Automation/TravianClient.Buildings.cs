using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private async Task WaitForPostUpgradeClickPageLoadAsync(CancellationToken cancellationToken)
    {
        Notify("[build:verbose] waiting for post-upgrade-click page load (DOMContentLoaded)");
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            Notify("[build:verbose] post-upgrade-click page load complete");
        }
        catch (Exception ex)
        {
            // Continue with the best available page state.
            Notify($"[build:verbose] post-upgrade-click load wait failed (continuing): {ex.GetType().Name}: {ex.Message}");
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after upgrade click.", cancellationToken);
    }

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
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
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
        var safetyCap = ComputeBuildingUpgradeSafetyCap(targetLevel);
        int? lastKnownLevel = null;
        var transientRetries = 0;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttempted = false;
        for (var iteration = 0; iteration < safetyCap; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {

            // Step 1: ensure dorf2 with fresh data.
            await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification on dorf2.", cancellationToken);

            // Step 2: read this slot's level.
            var slots = await ReadBuildingInfosAsync(cancellationToken);
            if (!slots.TryGetValue(slotId, out var info))
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
            var queueFingerprintBefore = BuildQueueFingerprint(await ReadBuildQueueAsync(cancellationToken));

            // Tribe/Plus-aware slot gate: if the build queue is full, defer this task back to
            // the program queue (queue_wait_seconds) rather than blocking the worker thread.
            var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, upgrades, cancellationToken);
            if (deferMessage is not null)
            {
                return deferMessage;
            }

            // Step 3: open the slot's build page.
            await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
            await PauseForManualStepIfVisibleAsync($"Manual verification on slot {slotId}.", cancellationToken);

            // Step 4: read the upgrade duration so we know how long to wait.
            var durationSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken) ?? 0;
            // Read the population increase this level grants before clicking (page changes after).
            var populationDelta = await ReadUpgradePopulationDeltaOnCurrentPageAsync(cancellationToken);

            // Step 5: click the "Upgrade to level N" button.
            var clicked = await ClickUpgradeToLevelButtonAsync(nextLevel, cancellationToken);
            if (!clicked)
            {
                var state = await DetectBuildPageStateAsync();
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

                var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
                if (!heroTransferAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    heroTransferAttempted = true;
                    if (await TryHeroResourceTransferForConstructionAsync($"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}", cancellationToken))
                    {
                        continue;
                    }
                }
                if (!constructionNpcTradeAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    constructionNpcTradeAttempted = true;
                    if (await TryNpcTradeForConstructionAsync($"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}", cancellationToken))
                    {
                        continue;
                    }
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                {
                    var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}",
                        ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                        cancellationToken);
                    return BuildUpgradeResourceBlockedResultMessage(snapshot);
                }
                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByQueue)
                {
                    var waitSeconds = ClampResourceWaitSeconds(actionability.QueueWaitSeconds);
                    if (ShouldDeferLongWait(waitSeconds))
                    {
                        return $"Slot {slotId} blocked by queue. queue_wait_seconds={waitSeconds}";
                    }
                    Notify($"Slot {slotId} blocked by queue. Waiting {waitSeconds}s. queue_wait_seconds={waitSeconds}");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                    continue;
                }
                return $"Slot {slotId}: could not find 'Upgrade to level {nextLevel}' button. Reason: {actionability.Outcome} ({actionability.Reason}). Upgrades performed: {upgrades}.";
            }

            upgrades += 1;
            Notify($"[build] click ok — slot={slotId} '{buildingName}' lvl {currentLevel} → {nextLevel} queued (duration~{durationSeconds}s, pop +{populationDelta?.ToString() ?? "?"})");
            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }
            await WaitForPostUpgradeClickPageLoadAsync(cancellationToken);
            var postClickWaitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, durationSeconds, cancellationToken);
            transientRetries = 0;
            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, currentLevel, queueFingerprintBefore, cancellationToken);
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
                        return $"Slot {slotId}: reached level {confirmedLevel} (target {targetLevel}). Upgrades performed: {upgrades}.";
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
                    return $"Slot {slotId}: reached level {nextLevel} (target {targetLevel}). Upgrades performed: {upgrades}.";
                }

                continue;
            }

            if (nextLevel < targetLevel)
            {
                continue;
            }

            return $"Slot {slotId}: upgrade to level {nextLevel} queued and still in progress. Target level {targetLevel}. Upgrades performed: {upgrades}. queue_wait_seconds={postClickWaitSeconds}";

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

    internal static int ComputeBuildingUpgradeSafetyCap(int targetLevel)
        => Math.Max(1, targetLevel + 5);

    private async Task<UpgradeResourceWaitSnapshot> ReadUpgradeResourceWaitSnapshotAsync(
        string blockedLabel,
        int fallbackWaitSeconds,
        CancellationToken cancellationToken)
    {
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
                HasAnyProduction(cachedProductionByHour) ? "cached_production" : "page_timer");
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
            "estimated_from_page");
        Notify(FormatUpgradeResourceWaitLog(liveSnapshot));
        return liveSnapshot;
    }

    private static UpgradeResourceWaitSnapshot BuildUpgradeResourceWaitSnapshotFromValues(
        string blockedLabel,
        IReadOnlyDictionary<string, long?> required,
        IReadOnlyDictionary<string, string> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        int fallbackWaitSeconds,
        string waitReasonWhenEstimated)
    {
        var values = new Dictionary<string, UpgradeResourceWaitValue>(StringComparer.OrdinalIgnoreCase);
        var longestFiniteSeconds = 0;
        var hasUnknownWait = false;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            required.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentRaw);
            productionByHour.TryGetValue(key, out var productionValue);
            var currentValue = TryParseResourceValue(currentRaw);
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

        return new UpgradeResourceWaitSnapshot(
            blockedLabel,
            values,
            resolvedWaitSeconds,
            hasUnknownWait && fallbackWaitSeconds <= 0 ? "recheck_needed" : resolvedWaitReason);
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

        return $"{snapshot.BlockedLabel}: waiting for resources | {string.Join(" | ", parts)} | queue_wait_seconds={snapshot.WaitSeconds} | wait_reason={snapshot.WaitReason}";
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

    private enum BuildPageState { Other, AtMaxLevel, WorkersBusy }

    private sealed record UpgradeResourceWaitSnapshot(
        string BlockedLabel,
        IReadOnlyDictionary<string, UpgradeResourceWaitValue> Values,
        int WaitSeconds,
        string WaitReason);

    private sealed record UpgradeResourceWaitValue(
        long? Required,
        long? Current,
        long? Missing,
        double? ProductionPerHour,
        int? WaitSeconds,
        string WaitReason);

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
                  return 'Other';
                }
                """);
            return raw switch
            {
                "AtMaxLevel" => BuildPageState.AtMaxLevel,
                "WorkersBusy" => BuildPageState.WorkersBusy,
                _ => BuildPageState.Other,
            };
        }
        catch
        {
            return BuildPageState.Other;
        }
    }

    private async Task<bool> ClickUpgradeToLevelButtonAsync(int nextLevel, CancellationToken cancellationToken)
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
            Notify($"Could not click upgrade button for level {nextLevel}. Last click error: {lastError}");
        }

        return false;
    }

    public async Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default)
    {
        Notify($"[build] upgrade-to-max starting — slot={slotId}");
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Building slot {slotId} is outside the building range.");
        }

        var safetyCap = Math.Max(1, maxAttempts);
        var upgrades = 0;
        var transientRetries = 0;
        var constructionNpcTradeAttempted = false;
        var heroTransferAttempted = false;

        for (var iteration = 0; iteration < safetyCap; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {

            // Step 1: ensure dorf2 with fresh data.
            await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification on dorf2.", cancellationToken);

            // Step 2: read current level + figure out max from catalog.
            var slots = await ReadBuildingInfosAsync(cancellationToken);
            if (!slots.TryGetValue(slotId, out var info))
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
            var queueFingerprintBefore = BuildQueueFingerprint(await ReadBuildQueueAsync(cancellationToken));

            // Tribe/Plus-aware slot gate: if the build queue is full, defer this task back to
            // the program queue (queue_wait_seconds) rather than blocking the worker thread.
            var deferMessage = await CheckQueueOrDeferAsync(ConstructionKind.Building, slotId, upgrades, cancellationToken);
            if (deferMessage is not null)
            {
                return deferMessage;
            }

            // Step 3: open the slot's build page.
            await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
            await PauseForManualStepIfVisibleAsync($"Manual verification on slot {slotId}.", cancellationToken);

            // Step 4: read upgrade duration so we know how long to wait.
            var durationSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken) ?? 0;
            // Read the population increase this level grants before clicking (page changes after).
            var populationDelta = await ReadUpgradePopulationDeltaOnCurrentPageAsync(cancellationToken);

            // Step 5: click "Upgrade to level N".
            var clicked = await ClickUpgradeToLevelButtonAsync(nextLevel, cancellationToken);
            if (!clicked)
            {
                var state = await DetectBuildPageStateAsync();
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

                var actionability = await AnalyzeUpgradeActionabilityAsync(slotId, cancellationToken, performClick: false);
                if (!heroTransferAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    heroTransferAttempted = true;
                    if (await TryHeroResourceTransferForConstructionAsync($"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}", cancellationToken))
                    {
                        continue;
                    }
                }
                if (!constructionNpcTradeAttempted && await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    constructionNpcTradeAttempted = true;
                    if (await TryNpcTradeForConstructionAsync($"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}", cancellationToken))
                    {
                        continue;
                    }
                }

                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByResources)
                {
                    var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Building slot {slotId} ({buildingName}) upgrade to level {nextLevel}",
                        ClampResourceWaitSeconds(actionability.QueueWaitSeconds),
                        cancellationToken);
                    return BuildUpgradeResourceBlockedResultMessage(snapshot);
                }
                if (actionability.Outcome == UpgradeAttemptOutcome.BlockedByQueue)
                {
                    var waitSeconds = ClampResourceWaitSeconds(actionability.QueueWaitSeconds);
                    if (ShouldDeferLongWait(waitSeconds))
                    {
                        return $"Slot {slotId} blocked by queue. queue_wait_seconds={waitSeconds}";
                    }
                    Notify($"Slot {slotId} blocked by queue. Waiting {waitSeconds}s. queue_wait_seconds={waitSeconds}");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                    continue;
                }
                return $"Slot {slotId}: could not find 'Upgrade to level {nextLevel}' button. Reason: {actionability.Outcome} ({actionability.Reason}). Upgrades performed: {upgrades}.";
            }

            upgrades += 1;
            Notify($"[build] click ok — slot={slotId} '{buildingName}' lvl {currentLevel} → {nextLevel} queued (duration~{durationSeconds}s, pop +{populationDelta?.ToString() ?? "?"})");
            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }
            await WaitForPostUpgradeClickPageLoadAsync(cancellationToken);
            var postClickWaitSeconds = await ReadQueuedBuildingWaitSecondsAsync(buildingName, durationSeconds, cancellationToken);
            transientRetries = 0;
            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, currentLevel, queueFingerprintBefore, cancellationToken);
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
                        return $"Slot {slotId}: reached max level {maxLevel}. Upgrades performed: {upgrades}.";
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
                    return $"Slot {slotId}: reached max level {maxLevel}. Upgrades performed: {upgrades}.";
                }

                continue;
            }

            if (nextLevel < maxLevel)
            {
                continue;
            }

            return $"Slot {slotId}: upgrade toward max queued and still in progress (next level {nextLevel}, max {maxLevel}). Upgrades performed: {upgrades}. queue_wait_seconds={postClickWaitSeconds}";

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
            var queueFingerprintBefore = BuildQueueFingerprint(await ReadBuildQueueAsync(cancellationToken));

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

            // Step 2: read build duration.
            var durationSeconds = await ReadUpgradeDurationSecondsOnCurrentPageAsync(cancellationToken) ?? 0;
            // Read the population the new building grants before clicking (page changes after).
            var populationDelta = await ReadUpgradePopulationDeltaOnCurrentPageAsync(cancellationToken);

            // Step 3: click the "Construct building" button (scoped to this gid when possible).
            var clicked = await ClickConstructBuildingButtonAsync(gid, cancellationToken);
            if (!clicked)
            {
                var waitAfterBusy = await WaitForConstructionSlotIfBusyAsync(ConstructionKind.Building, cancellationToken);
                if (waitAfterBusy > 0)
                {
                    continue;
                }

                var existingProgress = await DetectConstructProgressAsync(slotId, gid, buildingName, queueFingerprintBefore, cancellationToken);
                if (existingProgress.Started)
                {
                    return $"Queued {buildingName} in slot {slotId}. Evidence: {existingProgress.Evidence}.";
                }

                if (await CurrentPageLooksBlockedByResourcesAsync(cancellationToken))
                {
                    var snapshot = await ReadUpgradeResourceWaitSnapshotAsync(
                        $"Building slot {slotId} construct {buildingName}",
                        60,
                        cancellationToken);

                    if (!heroTransferAttempted)
                    {
                        heroTransferAttempted = true;
                        if (await TryHeroResourceTransferForConstructionAsync($"Building slot {slotId} construct {buildingName}", cancellationToken))
                        {
                            continue;
                        }
                    }

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

                await CaptureFailureArtifactsAsync($"construct-slot-{slotId}-gid-{gid}-no-click", cancellationToken);
                return $"Slot {slotId}: could not find 'Construct building' button for gid {gid}.";
            }

            if (populationDelta is int popDelta)
            {
                await AddPopulationToActiveVillageCacheAsync(popDelta, cancellationToken);
            }

            var progress = await WaitForBuildingLevelAdvanceAsync(slotId, 0, queueFingerprintBefore, cancellationToken);
            if (!progress.Advanced && !progress.QueuedOrInProgress)
            {
                // Final dorf2 probe: an instant-build server can finish a level-1 construct
                // before the queue ever shows it; any visible level > 0 means the click landed.
                var dorf2Level = await ProbeSlotLevelOnDorf2Async(slotId, cancellationToken);
                if (dorf2Level is int confirmedLevel && confirmedLevel >= 1)
                {
                    return $"Constructed {buildingName} in slot {slotId} (confirmed level {confirmedLevel} on dorf2).";
                }

                var waitMs = ComputePostActionWaitMs(durationSeconds);
                var waitSeconds = Math.Max(1, (int)Math.Ceiling(waitMs / 1000d));
                Notify($"Slot {slotId}: construct click did not confirm immediately ({progress.Evidence}). Deferring {waitSeconds}s before retry.");
                return $"Slot {slotId}: construct click did not confirm immediately ({progress.Evidence}). queue_wait_seconds={waitSeconds}";
            }

            return $"Queued {buildingName} in slot {slotId}. Evidence: {progress.Evidence}.";
        }

        return $"Slot {slotId}: hit safety cap while trying to queue {buildingName}.";
    }

    private async Task<(bool Started, string Evidence)> DetectConstructProgressAsync(
        int slotId,
        int gid,
        string buildingName,
        string queueFingerprintBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            var queueItems = await ReadBuildQueueAsync(cancellationToken);
            var queueFingerprintAfter = BuildQueueFingerprint(queueItems);
            if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
            {
                return (true, "queue changed");
            }

            var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken);
            var matchingActiveConstruction = activeConstructions.FirstOrDefault(item =>
                item.Kind != ConstructionKind.Resource
                && SameBuildingName(item.Name, buildingName));
            if (matchingActiveConstruction is not null)
            {
                return (true, $"active construction detected for {matchingActiveConstruction.Name}");
            }

            if (queueItems.Count > 0 && activeConstructions.Any(item => item.Kind != ConstructionKind.Resource))
            {
                return (true, "building queue has entries");
            }

            await GotoAsync(Paths.Buildings, cancellationToken);
            await PauseForManualStepIfVisibleAsync($"Manual verification while verifying slot {slotId} construction state.", cancellationToken);
            var slots = await ReadBuildingInfosAsync(cancellationToken);
            if (slots.TryGetValue(slotId, out var slotInfo))
            {
                var slotGid = ParseGidFromBuildingCode(slotInfo.BuildingCode);
                var sameBuilding = slotGid == gid || SameBuildingName(slotInfo.BuildingName, buildingName);
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
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
        catch (Exception ex)
        {
            Notify($"Construct page load wait failed before button scan: {ex.Message}");
            // Continue regardless; we'll still look for the button below.
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => /construct\\s+building|build\\s+building|bauen|bygg|costruisci|build/i.test(document.body.innerText || '')",
                null,
                new PageWaitForFunctionOptions { Timeout = 10000 });
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
              // Match `?a=N`, `&a=N`, `?gid=N`, `&gid=N`, plus Cyrillic 'а' (U+0430) used by some private servers.
              const otherGidRe = /[?&](?:[aа]|gid)=(\d+)/gi;
              for (const el of candidates) {
                const rawText = (el.textContent || '').replace(/\s+/g, ' ').trim();
                const text = rawText.toLowerCase();
                const classes = (el.className || '').toString().toLowerCase();
                if (rawText) seen.push({ text: rawText.slice(0, 60), classes: classes.slice(0, 60) });
                if (!/(construct|build|bauen|bygg)/.test(text)) continue;
                const disabled = el.disabled || classes.includes('disabled') || el.getAttribute('aria-disabled') === 'true';
                if (disabled) continue;
                const inOfficialPrimarySection = !!el.closest('.upgradeButtonsContainer .section1');
                const inOfficialSpeedupSection = !!el.closest('.upgradeButtonsContainer .section2');
                if (text.includes('npc') || text.includes('instant') || text.includes('faster') || classes.includes('gold') || classes.includes('purple') || classes.includes('videofeaturebutton') || inOfficialSpeedupSection) continue;
                const isUpgrade = /upgrade\s+to\s+level/i.test(text);
                if (isUpgrade) continue;
                // Travian wraps each constructable building in `#contract_building{gid}` (private servers
                // sometimes also use `#building{gid}` or [data-gid]). Search broadly.
                const wrapper = el.closest(
                  `#contract_building${gidText}, #building${gidText}, [id$="_building${gidText}"], [data-gid="${gidText}"], .gid${gidText}`
                );
                const wrapperMatchesGid = wrapper !== null;
                const onclick = (el.getAttribute('onclick') || '');
                const href = (el.getAttribute('href') || '');
                const value = (el.getAttribute('value') || '');
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
                const isConstruct = /construct\s+building/i.test(rawText) || /build\s+building/i.test(rawText);
                const rank = (wrapperMatchesGid ? 10 : 0) + (inOfficialPrimarySection ? 8 : 0) + (onclickMentionsGid ? 5 : 0) + (isConstruct ? 3 : 0) + (classes.includes('green') ? 2 : 0) + 1;
                matches.push({ index: candidates.indexOf(el), rank, text: rawText.slice(0, 60), gidContext: { wrapper: wrapperMatchesGid, onclick: onclickMentionsGid } });
              }
              matches.sort((a, b) => b.rank - a.rank);
              return JSON.stringify({ clicked: false, clickIndex: matches.length > 0 ? matches[0].index : null, matches: matches.slice(0, 5), seen: seen.slice(0, 20) });
            }
            """,
            new { gid });

        Notify($"Construct candidate scan: {rawJson}");
        try
        {
            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            if (!doc.RootElement.TryGetProperty("clickIndex", out var clickIndexProp)
                || clickIndexProp.ValueKind != JsonValueKind.Number
                || !clickIndexProp.TryGetInt32(out var clickIndex))
            {
                return false;
            }

            var clickTarget = _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(clickIndex);
            await clickTarget.ClickAsync(new LocatorClickOptions
            {
                Timeout = _config.TimeoutMs,
            });
            return true;
        }
        catch (Exception ex)
        {
            Notify($"Could not click construct candidate for gid {gid}: {ex.Message}");
            return false;
        }
    }

    public async Task<string> UpgradeAllTroopsAtSmithyAsync(CancellationToken cancellationToken = default)
    {
        Notify("UpgradeAllTroopsAtSmithyAsync started");

        var smithySlotId = await TryResolveSmithySlotIdAsync(cancellationToken);
        if (!smithySlotId.HasValue)
        {
            return "Smithy not found in this village. Build a Smithy first.";
        }
        Notify($"Smithy found at slot {smithySlotId.Value}.");

        const int safetyCap = 60;
        var totalUpgradeClicks = 0;
        var consecutiveEmptyReloads = 0;
        var consecutiveZeroDurationReloads = 0;

        for (var iter = 0; iter < safetyCap; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Always start each iteration on the smithy page.
            var smithyPath = Paths.BuildBySlot(smithySlotId.Value);
            if (!IsCurrentUrlForPath(smithyPath))
            {
                await GotoAsync(smithyPath, cancellationToken);
            }
            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }
            catch
            {
                // Continue.
            }
            await Task.Delay(400, cancellationToken);

            // Each Playwright call below is wrapped in RetryAsync so transient navigation
            // races (ERR_ABORTED / "frame was detached") that fire when Travian reloads or
            // the bot navigates mid-evaluation don't escalate into ALARMs. Up to 3 retries
            // with a short backoff, matching the policy used by other Buildings methods.

            // Check for "Improve the blacksmith" — smithy itself needs upgrade before more troops can be improved.
            var needsSmithyUpgrade = await RetryAsync(
                "Smithy: detect 'improve the blacksmith'",
                () => _page.EvaluateAsync<bool>(
                    "() => /improve\\s+the\\s+(blacksmith|smithy)/i.test(document.body.innerText || '')"),
                cancellationToken: cancellationToken);
            if (needsSmithyUpgrade)
            {
                Notify($"Smithy capacity exhausted (\"Improve the blacksmith\" detected). Upgrading slot {smithySlotId.Value} to max.");
                var upgradeResult = await UpgradeBuildingToMaxAsync(smithySlotId.Value, cancellationToken: cancellationToken);
                Notify($"Smithy upgrade result: {upgradeResult}");
                consecutiveEmptyReloads = 0;
                consecutiveZeroDurationReloads = 0;
                continue;
            }

            // Try to click an Upgrade button for any troop.
            var clickResult = await RetryAsync(
                "Smithy: click first upgrade button",
                () => ClickFirstSmithyUpgradeButtonAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (clickResult.Clicked)
            {
                totalUpgradeClicks += 1;
                consecutiveEmptyReloads = 0;
                consecutiveZeroDurationReloads = 0;
                Notify($"Smithy: clicked upgrade for '{clickResult.Label}'. Total clicks: {totalUpgradeClicks}.");
                // Brief pause then reload to refresh button state.
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var smithyFullyDeveloped = await RetryAsync(
                "Smithy: check fully developed",
                () => IsSmithyFullyDevelopedAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (smithyFullyDeveloped)
            {
                Notify($"Smithy: fully developed detected. Total clicks: {totalUpgradeClicks}.");
                await GotoAsync(Paths.Buildings, cancellationToken);
                return $"Smithy: upgraded {totalUpgradeClicks} troop(s). All done.";
            }

            // No clickable upgrade button. Inspect research-in-progress duration.
            var durationSeconds = await RetryAsync(
                "Smithy: read research duration",
                () => ReadSmithyResearchDurationSecondsAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (durationSeconds is int dur)
            {
                // Also reload when Travian shows its own "auto-reload failed" marker on the timer:
                // the duration may have ticked into a negative value rather than exactly zero, so
                // dur<=0 alone misses it. IsPageMarkedStaleAsync catches the visual .timer.no-reload
                // indicator regardless of the parsed duration.
                if (dur <= 0 || await IsPageMarkedStaleAsync())
                {
                    consecutiveZeroDurationReloads += 1;
                    if (consecutiveZeroDurationReloads >= 3)
                    {
                        return $"Smithy: research timer stuck at 00:00:00 after 3 reloads. Manual review needed. Upgrades clicked: {totalUpgradeClicks}.";
                    }
                    Notify($"Smithy: timer at 00:00:00 or page marked stale, reloading (attempt {consecutiveZeroDurationReloads}/3).");
                    await TryReloadSmithyAsync(cancellationToken);
                    continue;
                }
                consecutiveZeroDurationReloads = 0;
                var waitSec = Math.Clamp(dur + 1, 2, 600);
                if (ShouldDeferLongWait(waitSec))
                {
                    Notify($"Smithy: research in progress, deferring for {waitSec}s.");
                    return $"Smithy: research in progress. queue_wait_seconds={waitSec}";
                }

                // Emit queue_wait_seconds even on the inline path so the desktop dashboard's
                // log stream can mirror the countdown to its smithy timer. The task itself
                // does not defer (waitSec is below the queue wait threshold).
                Notify($"Smithy: research in progress, waiting {waitSec}s. queue_wait_seconds={waitSec}");
                await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken);
                await TryReloadSmithyAsync(cancellationToken);
                continue;
            }

            // No buttons, no timer. Reload up to 3 times before declaring done.
            consecutiveEmptyReloads += 1;
            if (consecutiveEmptyReloads >= 3)
            {
                Notify($"Smithy: no upgrade buttons after 3 reloads. All troops appear to be at max. Total clicks: {totalUpgradeClicks}.");
                await GotoAsync(Paths.Buildings, cancellationToken);
                return $"Smithy: upgraded {totalUpgradeClicks} troop(s). All done.";
            }
            Notify($"Smithy: no buttons visible, reload {consecutiveEmptyReloads}/3.");
            await TryReloadSmithyAsync(cancellationToken);
        }

        return $"Smithy: hit safety cap of {safetyCap} iterations after {totalUpgradeClicks} click(s).";
    }

    private async Task TryReloadSmithyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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

        var activeTimers = (await ReadSmithyResearchDurationsSecondsAsync(cancellationToken))
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToList();
        var remainingSeconds = activeTimers.Count > 0 ? activeTimers[0] : (int?)null;

        return new SmithyUpgradeStatus(
            SmithyExists: true,
            SmithySlotId: smithySlotId.Value,
            ActiveUpgradeCount: activeTimers.Count,
            RemainingSeconds: remainingSeconds,
            ActiveUpgradeRemainingSeconds: activeTimers,
            RemainingText: remainingSeconds is > 0 ? FormatDuration(remainingSeconds.Value) : "Ready",
            StatusText: activeTimers.Count > 0
                ? $"Smithy upgrade{(activeTimers.Count == 1 ? string.Empty : "s")} active."
                : "Ready.");
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

        var activeTimers = (await ReadSmithyResearchDurationsSecondsAsync(cancellationToken))
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToList();
        if (activeTimers.Count <= 0)
        {
            return "Smithy queue test: ready. No active Smithy upgrade found on the current page.";
        }

        var timersText = string.Join(", ", activeTimers.Select(FormatDuration));
        return $"Smithy queue test: active={activeTimers.Count}, next={FormatDuration(activeTimers[0])}, timers=[{timersText}]";
    }

    private static int? ResolveKnownSmithySlotId(IReadOnlyList<Building>? knownBuildings)
    {
        return knownBuildings?
            .FirstOrDefault(item =>
                item.SlotId is > 0
                && (item.Gid == 12 || string.Equals(item.Name, "Smithy", StringComparison.OrdinalIgnoreCase)))
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
            var smithyEntry = slots.FirstOrDefault(kvp =>
                ParseGidFromBuildingCode(kvp.Value.BuildingCode) == 12 && kvp.Value.Level > 0);
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

    private async Task<(bool Clicked, string Label)> ClickFirstSmithyUpgradeButtonAsync(CancellationToken cancellationToken)
    {
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const candidates = Array.from(document.querySelectorAll(
                'button, input[type="submit"], input[type="button"], a, div.addHoverClick, div.button-container'
              ));
              for (const el of candidates) {
                const rawText = (el.textContent || '').replace(/\s+/g, ' ').trim();
                const text = rawText.toLowerCase();
                if (!text) continue;
                if (!/^upgrade\b/.test(text)) continue;
                if (/upgrade\s+to\s+level/i.test(text)) continue; // skip building-level upgrade buttons
                if (/improve/i.test(text)) continue;
                const classes = (el.className || '').toString().toLowerCase();
                if (el.disabled || classes.includes('disabled') || el.getAttribute('aria-disabled') === 'true') continue;
                if (classes.includes('gold') || /npc|instant/i.test(text)) continue;
                // Prefer a unit-row container if present.
                const row = el.closest('.research, .unit, .researchUnit, tr, li, .contract');
                let label = rawText;
                if (row) {
                  const heading = row.querySelector('.title, h4, h3, .unitName, .name, td.desc');
                  if (heading && (heading.textContent || '').trim()) {
                    label = heading.textContent.replace(/\s+/g, ' ').trim();
                  }
                }
                el.click();
                return JSON.stringify({ clicked: true, label: label.slice(0, 80) });
              }
              return JSON.stringify({ clicked: false, label: '' });
            }
            """);

        try
        {
            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var clicked = doc.RootElement.TryGetProperty("clicked", out var c) && c.GetBoolean();
            var label = doc.RootElement.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            return (clicked, label);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private async Task<bool> IsSmithyFullyDevelopedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const rows = Array.from(document.querySelectorAll('.build_details.researches .research'));
                  if (rows.length === 0) {
                    return false;
                  }

                  let fullyDevelopedRows = 0;
                  for (const row of rows) {
                    const ctaText = clean(row.querySelector('.cta')?.textContent || '');
                    const levelText = clean(row.querySelector('.level')?.textContent || '');
                    if (ctaText.includes('fully developed') || ctaText.includes('fully researched')) {
                      fullyDevelopedRows += 1;
                      continue;
                    }

                    if (levelText.includes('level 20') && ctaText.includes('none')) {
                      fullyDevelopedRows += 1;
                    }
                  }

                  return fullyDevelopedRows > 0 && fullyDevelopedRows === rows.length;
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private async Task<int?> ReadSmithyResearchDurationSecondsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var values = await ReadSmithyResearchDurationsSecondsAsync(cancellationToken);
            return values.Count > 0 ? values.Min() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<int>> ReadSmithyResearchDurationsSecondsAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            var values = await _page.EvaluateAsync<int[]>(
                """
                () => {
                  const parseSeconds = (value) => {
                    if (!value) return null;
                    const text = String(value).trim();
                    if (!text) return null;
                    if (/^\d+$/.test(text)) {
                      return parseInt(text, 10);
                    }

                    const match = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(text);
                    if (!match) return null;
                    const hasHours = match[3] !== undefined;
                    const hh = hasHours ? parseInt(match[1], 10) : 0;
                    const mm = hasHours ? parseInt(match[2], 10) : parseInt(match[1], 10);
                    const ss = hasHours ? parseInt(match[3], 10) : parseInt(match[2], 10);
                    return hh * 3600 + mm * 60 + ss;
                  };

                  const progressTimers = Array.from(document.querySelectorAll('table.under_progress .timer'));
                  const progressValues = progressTimers
                    .map(el => parseSeconds(el.getAttribute('value')) ?? parseSeconds(el.textContent))
                    .filter(value => value !== null);
                  if (progressValues.length > 0) {
                    return progressValues
                      .map(value => Number(value))
                      .filter(value => Number.isFinite(value) && value > 0)
                      .sort((a, b) => a - b);
                  }

                  const scoped = Array.from(document.querySelectorAll(
                    '.under_progress .timer, .under_progress [id^="timer"], .build_details.researches .timer, .build_details.researches [id^="timer"], .research .timer, .research [id^="timer"], .researchDuration, .duration'
                  ));
                  const values = scoped
                    .map(el => parseSeconds(el.getAttribute?.('value')) ?? parseSeconds(el.textContent))
                    .filter(value => value !== null);
                  if (values.length > 0) {
                    return values
                      .map(value => Number(value))
                      .filter(value => Number.isFinite(value) && value > 0)
                      .sort((a, b) => a - b);
                  }

                  return [];
                }
                """);

            return values
                .Where(value => value > 0)
                .OrderBy(value => value)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private bool ShouldDeferLongWait(int waitSeconds)
    {
        if (waitSeconds <= 0)
        {
            return false;
        }

        var mode = _config.QueueWaitThresholdMode?.Trim();
        if (string.Equals(mode, "smart", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!int.TryParse(mode, out var thresholdSeconds) || thresholdSeconds < 0)
        {
            thresholdSeconds = 10;
        }

        return waitSeconds > thresholdSeconds;
    }

    public async Task<IReadOnlyList<ServerBuildChoice>> ReadAvailableBuildingsForSlotAsync(int slotId, CancellationToken cancellationToken = default)
    {
        Notify("[build:verbose] ReadAvailableBuildingsForSlotAsync started");
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
            || SameBuildingName(item.BuildingName, name));
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
            var normalized = NormalizeBuildingName(candidate ?? string.Empty);
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

    internal static int ClampResourceWaitSeconds(int? detectedSeconds)
    {
        const int min = 30;
        const int fallback = 5 * 60;
        const int max = 12 * 60 * 60;
        if (detectedSeconds is not int s || s <= 0) return fallback;
        if (s < min) return min;
        if (s > max) return max;
        return s + 1;
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
            return _cachedActiveConstructions;
        }

        LogFunctionStarted();
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading active constructions.", cancellationToken);

        var raw = await ReadActiveConstructionsOnCurrentPageAsync();
        if (raw.Count == 0
            && IsOfficialTravianServer()
            && allowNavigationToBuildings
            && !IsCurrentUrlForPath(Paths.Buildings))
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            raw = await ReadActiveConstructionsOnCurrentPageAsync();
        }

        var result = raw
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new ActiveConstruction(
                Kind: i.Kind switch
                {
                    "Resource" => ConstructionKind.Resource,
                    "Building" => ConstructionKind.Building,
                    _ => ConstructionKind.Unknown
                },
                Name: i.Name!,
                Level: i.Level,
                TimeLeftSeconds: i.TimeLeftSeconds ?? ParseDurationToSeconds(i.FinishAtText),
                FinishAtText: i.FinishAtText))
            .ToList();

        _cachedActiveConstructions = result;
        _cachedActiveConstructionsAt = DateTimeOffset.UtcNow;
        return result;

        async Task<List<ActiveConstructionJs>> ReadActiveConstructionsOnCurrentPageAsync()
        {
            var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const items = [];
              const lis = document.querySelectorAll('.boxes.buildingList ul li, .buildingList ul li');
              const seen = new Set();
              for (const li of lis) {
                const nameEl = li.querySelector('.name');
                if (!nameEl) continue;
                const fullName = (nameEl.textContent || '').replace(/\s+/g, ' ').trim();
                if (!fullName || seen.has(fullName)) continue;
                seen.add(fullName);

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

                items.push({ kind, name: baseName, level, timeLeftSeconds: timeLeft, finishAtText: finishText });
              }
              return JSON.stringify(items);
            }
            """);

            return string.IsNullOrWhiteSpace(rawJson)
                ? new List<ActiveConstructionJs>()
                : JsonSerializer.Deserialize<List<ActiveConstructionJs>>(rawJson) ?? new List<ActiveConstructionJs>();
        }
    }

    private bool IsOfficialTravianServer()
    {
        return Uri.TryCreate(_config.BaseUrl, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("travian.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".travian.com", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ConstructionSlotStatus> EvaluateConstructionSlotsAsync(
        string tribe,
        bool travianPlusActive,
        CancellationToken cancellationToken = default,
        bool allowNavigationToBuildings = true)
    {
        LogFunctionStarted();
        var active = await ReadActiveConstructionsAsync(cancellationToken, allowNavigationToBuildings);
        return ComputeConstructionSlotStatus(active, tribe, travianPlusActive);
    }

    internal static ConstructionSlotStatus ComputeConstructionSlotStatus(
        IReadOnlyList<ActiveConstruction> active,
        string tribe,
        bool travianPlusActive)
    {
        var isRomans = string.Equals(tribe, "Romans", StringComparison.OrdinalIgnoreCase);
        var resourceUsed = active.Count(a => a.Kind == ConstructionKind.Resource);
        var buildingUsed = active.Count(a => a.Kind != ConstructionKind.Resource);

        bool canResource;
        bool canBuilding;
        int resourceMax;
        int buildingMax;

        if (isRomans)
        {
            resourceMax = 1;
            buildingMax = travianPlusActive ? 2 : 1;
            canResource = resourceUsed < resourceMax;
            canBuilding = buildingUsed < buildingMax;
        }
        else
        {
            resourceMax = 1;
            buildingMax = travianPlusActive ? 2 : 1;
            var totalUsed = active.Count;
            canResource = canBuilding = totalUsed < buildingMax;
        }

        int? shortest = null;
        foreach (var item in active)
        {
            if (item.TimeLeftSeconds is int s && s > 0)
            {
                shortest = shortest is null ? s : Math.Min(shortest.Value, s);
            }
        }

        return new ConstructionSlotStatus(
            Active: active,
            ResourceSlotsUsed: resourceUsed,
            BuildingSlotsUsed: buildingUsed,
            ResourceSlotsMax: resourceMax,
            BuildingSlotsMax: buildingMax,
            CanStartResource: canResource,
            CanStartBuilding: canBuilding,
            ShortestWaitSeconds: shortest);
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
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading Travian Plus status.", cancellationToken);

        return await _page.EvaluateAsync<bool>(
            """
            () => {
              // Official Travian (T4.6): the village quick-links bar is a Travian Plus feature.
              // The quick-link buttons are styled "green" when Plus is active and "gold" when it
              // is not (the disabled state is unrelated — it depends on the action's availability).
              // This markup does not exist on SS, so SS falls through to the legacy check below.
              const quickLinks = Array.from(document.querySelectorAll('a[data-dragid^="villageQuickLinks"], a[data-load-tooltip-data*="MarketplaceSendResources"]'));
              if (quickLinks.length > 0) {
                const classes = quickLinks.map(node => (node.className || '').toString()).join(' ');
                if (/\bgreen\b/.test(classes)) return true;   // Plus active
                if (/\bgold\b/.test(classes)) return false;   // Plus not active
              }

              const box = document.querySelector('#sidebarBoxLinklist');
              if (!box) return false;
              const html = box.innerHTML || '';
              if (html.includes('plusDialog')) return false;
              if (/editWhite\s+green/.test(html)) return true;
              if (/spieler\.php\?s=2/.test(html)) return true;
              return true;
            }
            """);
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
                if (!skipNavigationIfOnExpectedSlot || ExtractSlotIdFromUrl(_page.Url) != slotId)
                {
                    await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
                }
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the upgrade page.", cancellationToken);
                await EnsureLoggedInAsync();
                await EnsureExpectedBuildSlotPageAsync(slotId, "analyze upgrade", cancellationToken);
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

                      const readServerNow = () => {
                        const candidates = ['#servertime .timeStandard', '#servertime', '.serverTime', '#stockBarTimer'];
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
                        const combined = `${text} ${classes} ${controlText} ${controlClasses} ${href} ${formAction}`;
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

                        if (!hasUpgradeSignals || isGold || isSpeedup || looksLikePrimaryNoise || displayText.length === 0) {
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
                          clickOrder.push({ candidateIndex, text: displayText, classes: `${classes} ${controlClasses}`, inUpgradeContainer, inOfficialPrimarySection });
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
                        if (isResourceBlock) {
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
                            reason: 'Upgrade blocked: not enough resources yet (upgradeBlocked panel).',
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
                }, cancellationToken: cancellationToken);
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
                    await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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
            var queueFingerprintAfter = BuildQueueFingerprint(queueItems);
            if (!string.Equals(queueFingerprintBefore, queueFingerprintAfter, StringComparison.Ordinal))
            {
                return new UpgradeProgressResult(false, true, "queue changed");
            }

            if (queueItems.Count > 0)
            {
                return new UpgradeProgressResult(false, true, "queue has entries");
            }

            var activeConstructions = await ReadActiveConstructionsAsync(cancellationToken);
            if (activeConstructions.Any(item => item.Kind != ConstructionKind.Resource))
            {
                return new UpgradeProgressResult(false, true, "active building construction detected");
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
                return ComputeUpgradeWaitSeconds(fallbackSeconds);
            }

            var matchingTimers = buildingTimers
                .Where(item => SameBuildingName(item.Name, buildingName))
                .Select(item => item.TimeLeftSeconds!.Value)
                .ToList();
            if (matchingTimers.Count > 0)
            {
                return ComputeUpgradeWaitSeconds(matchingTimers.Min());
            }

            return ComputeUpgradeWaitSeconds(buildingTimers.Min(item => item.TimeLeftSeconds!.Value));
        }
        catch
        {
            return ComputeUpgradeWaitSeconds(fallbackSeconds);
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
                .Where(item => item.Kind != ConstructionKind.Resource && SameBuildingName(item.Name, buildingName))
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
            .Where(building => building.Gid == gid || SameBuildingName(building.Name, name))
            .ToList();
        var duplicateAllowed = gid is 23 or 38 or 39;
        var wallGid = gid is 31 or 32 or 33 or 42 or 43;
        if ((gid is 29 or 30) && status.IsCapital == true)
        {
            throw new InvalidOperationException($"{name} cannot be built in the capital.");
        }

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
            "blacksmith" => "smithy",
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

    internal static string BuildQueueIdentityFingerprint(IReadOnlyList<BuildQueueItem> queue)
    {
        if (queue.Count == 0)
        {
            return "empty";
        }

        return string.Join(
            " || ",
            queue
                .Take(5)
                .Select(item => item.Text.Trim()));
    }

    internal static IReadOnlyList<Building> ParseBuildingOverviewHtmlForTests(string html)
    {
        var slots = ExtractBuildingSlotHtml(html)
            .Select((slotHtml, index) =>
            {
                var className = ReadAttribute(slotHtml, "class") ?? string.Empty;
                var labelText = CleanHtmlText(Regex.Match(slotHtml, @"<div\b[^>]*class=[""'][^""']*\blabelLayer\b[^""']*[""'][^>]*>(?<text>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["text"].Value);
                var link = Regex.Match(slotHtml, @"<a\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["attrs"].Value;
                var dataName = ReadAttribute(slotHtml, "data-name") ?? string.Empty;
                var dataLevel = ReadAttribute(link, "data-level") ?? ReadAttribute(slotHtml, "data-level") ?? string.Empty;
                return new BuildingOverviewSlotSnapshot
                {
                    Index = index,
                    ClassName = className,
                    OuterHtml = slotHtml,
                    LevelText = labelText,
                    DataLevelText = dataLevel,
                    DataNameText = dataName,
                    Text = CleanHtmlText(slotHtml),
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

    internal static HtmlButtonCandidate? SelectUpgradeButtonCandidateFromHtmlForTests(string html, int nextLevel)
    {
        var candidates = ExtractButtonCandidates(html);
        var expectedText = $"Upgrade to level {nextLevel}";
        return candidates
            .Where(candidate => candidate.Text.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !candidate.Disabled && !candidate.IsSpeedup && !candidate.IsGold)
            .OrderByDescending(candidate => candidate.InOfficialPrimarySection)
            .ThenByDescending(candidate => candidate.Classes.Contains("green", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    internal static IReadOnlyList<HtmlButtonCandidate> ExtractButtonCandidatesFromHtmlForTests(string html)
    {
        return ExtractButtonCandidates(html);
    }

    internal static HtmlButtonCandidate? SelectConstructButtonCandidateFromHtmlForTests(string html, int gid)
    {
        var gidText = gid.ToString(CultureInfo.InvariantCulture);
        return ExtractButtonCandidates(html)
            .Where(candidate => candidate.Text.Contains("Construct building", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !candidate.Disabled && !candidate.IsSpeedup && !candidate.IsGold)
            .Where(candidate => string.Equals(candidate.WrapperGid, gidText, StringComparison.Ordinal)
                || candidate.OnClick.Contains($"gid={gidText}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => string.Equals(candidate.WrapperGid, gidText, StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.InOfficialPrimarySection)
            .ThenByDescending(candidate => candidate.Classes.Contains("green", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    internal static IReadOnlyDictionary<string, long?> ReadConstructionCostFromHtmlForTests(string html)
    {
        var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, cssClass) in new[] { ("wood", "r1"), ("clay", "r2"), ("iron", "r3"), ("crop", "r4") })
        {
            var match = Regex.Match(
                html,
                $@"<i\b[^>]*class=[""'][^""']*\b{cssClass}Big\b[^""']*[""'][^>]*>\s*</i>\s*<span\b[^>]*class=[""'][^""']*\bvalue\b[^""']*[""'][^>]*>(?<value>.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result[key] = match.Success ? TryParseResourceValue(CleanHtmlText(match.Groups["value"].Value)) : null;
        }

        return result;
    }

    internal static int? ReadPrimaryBuildDurationSecondsFromHtmlForTests(string html)
    {
        var source = html ?? string.Empty;
        var section1Index = Regex.Match(
            source,
            @"<div\b[^>]*class=[""'][^""']*\bsection1\b[^""']*[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (section1Index.Success)
        {
            var section2Index = Regex.Match(
                source[section1Index.Index..],
                @"<div\b[^>]*class=[""'][^""']*\bsection2\b[^""']*[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            source = section2Index.Success
                ? source.Substring(section1Index.Index, section2Index.Index)
                : source[section1Index.Index..];
        }

        var match = Regex.Match(
            source,
            @"<div\b[^>]*class=[""'][^""']*\bduration\b[^""']*[""'][^>]*>.*?<span\b[^>]*class=[""'][^""']*\bvalue\b[^""']*[""'][^>]*>(?<value>.*?)</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? ParseDurationToSeconds(CleanHtmlText(match.Groups["value"].Value)) : null;
    }

    internal sealed record HtmlButtonCandidate(
        string Text,
        string Classes,
        string OnClick,
        string? WrapperGid,
        bool Disabled,
        bool IsGold,
        bool IsSpeedup,
        bool InOfficialPrimarySection);

    private static IReadOnlyList<HtmlButtonCandidate> ExtractButtonCandidates(string html)
    {
        var candidates = new List<HtmlButtonCandidate>();
        var sourceHtml = html ?? string.Empty;
        foreach (Match match in Regex.Matches(sourceHtml, @"<button\b(?<attrs>[^>]*)>(?<text>.*?)</button>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups["attrs"].Value;
            var text = CleanHtmlText(ReadAttribute(attrs, "value") ?? match.Groups["text"].Value);
            var classes = ReadAttribute(attrs, "class") ?? string.Empty;
            var onclick = System.Net.WebUtility.HtmlDecode(ReadAttribute(attrs, "onclick") ?? string.Empty);
            var before = sourceHtml[..match.Index];
            var afterLastWrapper = before.LastIndexOf("contract_building", StringComparison.OrdinalIgnoreCase);
            string? wrapperGid = null;
            if (afterLastWrapper >= 0)
            {
                var wrapperMatch = Regex.Match(before[afterLastWrapper..], @"contract_building(?<gid>\d{1,2})", RegexOptions.IgnoreCase);
                wrapperGid = wrapperMatch.Success ? wrapperMatch.Groups["gid"].Value : null;
            }

            var lastSection1 = LastSectionIndex(before, "section1");
            var lastSection2 = LastSectionIndex(before, "section2");
            var inPrimary = lastSection1 > lastSection2;
            var lowerCombined = $"{text} {classes} {onclick}".ToLowerInvariant();
            candidates.Add(new HtmlButtonCandidate(
                text,
                classes,
                onclick,
                wrapperGid,
                HasDisabledAttribute(attrs) || classes.Contains("disabled", StringComparison.OrdinalIgnoreCase),
                lowerCombined.Contains("gold") || lowerCombined.Contains("npc") || lowerCombined.Contains("instant"),
                lastSection2 > lastSection1 || lowerCombined.Contains("purple") || lowerCombined.Contains("videofeature") || lowerCombined.Contains("faster"),
                inPrimary));
        }

        return candidates;
    }

    private static bool HasDisabledAttribute(string attributes)
    {
        return Regex.IsMatch(
            attributes ?? string.Empty,
            @"(?:^|\s)disabled(?:\s|=|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static int LastSectionIndex(string html, string sectionClass)
    {
        var matches = Regex.Matches(
            html,
            @$"<div\b[^>]*class=[""'][^""']*\b{Regex.Escape(sectionClass)}\b[^""']*[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return matches.Count == 0 ? -1 : matches[^1].Index;
    }

    private static IReadOnlyList<string> ExtractBuildingSlotHtml(string html)
    {
        return Regex.Matches(
                html ?? string.Empty,
                @"<div\b[^>]*class=[""'][^""']*\bbuildingSlot\b[^""']*[""'][\s\S]*?(?=<div\b[^>]*class=[""'][^""']*\bbuildingSlot\b|<div\s+id=[""']sidebar|$)",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToList();
    }

    private static string? ReadAttribute(string htmlOrAttributes, string attributeName)
    {
        var match = Regex.Match(
            htmlOrAttributes ?? string.Empty,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*([""'])(?<value>.*?)\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private static string CleanHtmlText(string value)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<.*?>", " ", RegexOptions.Singleline));
        return string.Join(" ", decoded.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
    }

    private async Task EnsureExpectedBuildSlotPageAsync(int slotId, string operationLabel, CancellationToken cancellationToken = default)
    {
        var currentSlotId = ExtractSlotIdFromUrl(_page.Url);
        if (currentSlotId != slotId)
        {
            // A prior read in the upgrade flow (ReadActiveConstructionsAsync / ReadBuildQueueAsync)
            // can navigate away from the build slot page to dorf2.php. Re-open the slot so the
            // upgrade click targets the right building instead of failing on the wrong page.
            // (Notably triggered on official Travian, where build.php?id=N redirects to add &gid=.)
            await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
            await EnsureLoggedInAsync();
            currentSlotId = ExtractSlotIdFromUrl(_page.Url);
            if (currentSlotId != slotId)
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

    private async Task<bool> WaitForBuildSlotContextAsync(int slotId, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.WaitForFunctionAsync(
                """
                ({ slotId }) => {
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
        ["g12"] = "Smithy",
        ["g13"] = "Armoury",
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
        ["g46"] = "Hospital",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> NormalizedBuildingCodesByName = new(() =>
    {
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in TravianBuildings)
        {
            var normalized = NormalizeBuildingName(entry.Value);
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

}
