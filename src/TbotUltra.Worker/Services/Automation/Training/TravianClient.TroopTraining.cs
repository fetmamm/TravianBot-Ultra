using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private sealed record TroopTrainingRequest(
        TroopTrainingBuildingType BuildingType,
        string BuildingName,
        bool IsEnabled,
        string TroopType,
        string MaxQueueMode,
        string AmountMode,
        int KeepResourcesPercent,
        string RunMode,
        int MinimumTroops,
        int MinimumResourcesPercent,
        int TimedMinMinutes,
        int TimedMaxMinutes,
        bool CheckWood,
        bool CheckClay,
        bool CheckIron,
        bool CheckCrop);

    private sealed record TroopTrainingCandidate(
        TroopTrainingRequest Request,
        TroopTrainingQueueStatus QueueStatus,
        int QueueRemainingSeconds,
        int? QueueLimitSeconds);

    private sealed record TroopTrainingResourceSnapshot(
        IReadOnlyDictionary<string, long> Resources,
        ResourceCapacitySnapshot Capacities,
        IReadOnlyDictionary<string, double?> ProductionByHour);

    private sealed record TroopTrainingAttemptOutcome(
        bool Success,
        string Message,
        int? WaitSeconds = null);

    private sealed record TroopTrainingWaitEstimate(
        int WaitSeconds,
        string WaitReason,
        IReadOnlyDictionary<string, long> RequiredResources,
        IReadOnlyDictionary<string, double?> ProductionByHour);

    public async Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadTroopTrainingQueuesAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var statuses = new List<TroopTrainingQueueStatus>();
        var enabledBuildingTypes = new List<TroopTrainingBuildingType>(3);
        if (_config.TroopTrainingBarracksEnabled)
        {
            enabledBuildingTypes.Add(TroopTrainingBuildingType.Barracks);
        }

        if (_config.TroopTrainingStableEnabled)
        {
            enabledBuildingTypes.Add(TroopTrainingBuildingType.Stable);
        }

        if (_config.TroopTrainingWorkshopEnabled)
        {
            enabledBuildingTypes.Add(TroopTrainingBuildingType.Workshop);
        }

        Notify($"[troops:verbose] queue scan limited to {enabledBuildingTypes.Count} enabled building(s).");

        // Reuse the queue statuses build_troops just read on this village (it already visited each
        // enabled troop building), so the post-build refresh doesn't re-navigate to the same pages.
        Dictionary<TroopTrainingBuildingType, TroopTrainingQueueStatus>? recentSnapshot = null;
        if (_session.TroopQueueSnapshotByBuilding is { Count: > 0 }
            && DateTimeOffset.UtcNow - _session.TroopQueueSnapshotAt <= TroopTrainingQueueSnapshotMaxAge)
        {
            var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            var activeVillageKey = BuildTroopQueueVillageKey(activeCoords.X, activeCoords.Y);
            if (activeVillageKey is not null
                && string.Equals(activeVillageKey, _session.TroopQueueSnapshotVillageKey, StringComparison.OrdinalIgnoreCase))
            {
                recentSnapshot = _session.TroopQueueSnapshotByBuilding;
            }
        }

        IReadOnlyList<Building>? buildings = knownBuildings;
        foreach (var buildingType in enabledBuildingTypes)
        {
            if (recentSnapshot is not null && recentSnapshot.TryGetValue(buildingType, out var recentStatus))
            {
                var snapshotAgeSeconds = (int)(DateTimeOffset.UtcNow - _session.TroopQueueSnapshotAt).TotalSeconds;
                Notify($"[troops:verbose] queue scan:reusing {recentStatus.BuildingName} queue from build_troops read {snapshotAgeSeconds}s ago.");
                statuses.Add(recentStatus);
                continue;
            }

            Notify($"[troops:verbose] queue scan:reading {buildingType}.");
            if (buildings is null)
            {
                buildings = (await ReadBuildingsStatusAsync(cancellationToken)).Buildings;
                Notify($"[troops:verbose] queue scan:using {buildings.Count} known building(s).");
            }

            var queueStatus = await ReadTroopTrainingQueueStatusAsync(buildings, buildingType, cancellationToken);
            statuses.Add(queueStatus);
            Notify($"[troops:verbose] queue scan:{queueStatus.BuildingName} exists={queueStatus.Exists}, remaining={(queueStatus.RemainingSeconds is > 0 ? queueStatus.RemainingText : "Ready")}.");
        }

        return statuses;
    }

    private static readonly TimeSpan TroopTrainingQueueSnapshotMaxAge = TimeSpan.FromSeconds(90);

    // Called wherever build_troops reads a troop building's queue (candidate scan + after submit) so the
    // post-build refresh can reuse the data instead of navigating back to the same building pages.
    private void SaveTroopTrainingQueueSnapshot(VillageStatus status, TroopTrainingQueueStatus queueStatus)
    {
        var villageKey = BuildTroopQueueVillageKey(status.ActiveVillageCoordX, status.ActiveVillageCoordY);
        if (villageKey is null)
        {
            Notify($"[troops:verbose] queue snapshot not cached for '{status.ActiveVillage}': stable village coordinates are unavailable.");
            return;
        }

        if (_session.TroopQueueSnapshotByBuilding is null
            || !string.Equals(_session.TroopQueueSnapshotVillageKey, villageKey, StringComparison.OrdinalIgnoreCase))
        {
            _session.TroopQueueSnapshotVillageKey = villageKey;
            _session.TroopQueueSnapshotByBuilding = new Dictionary<TroopTrainingBuildingType, TroopTrainingQueueStatus>();
        }

        _session.TroopQueueSnapshotByBuilding[queueStatus.BuildingType] = queueStatus;
        _session.TroopQueueSnapshotAt = DateTimeOffset.UtcNow;
    }

    private static string? BuildTroopQueueVillageKey(int? coordX, int? coordY)
        => coordX.HasValue && coordY.HasValue ? $"xy:{coordX.Value}|{coordY.Value}" : null;

    public async Task<string> BuildTroopsAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        Notify("[troops:verbose]loading village status.");

        var status = await ReadVillageStatusAsync(cancellationToken);
        Notify($"[troops:verbose]activeVillage='{status.ActiveVillage}', tribe='{status.Tribe}', resources={string.Join(", ", status.Resources.Select(pair => $"{pair.Key}={pair.Value}"))}.");
        if (!TroopCatalog.IsKnownTribe(status.Tribe))
        {
            Notify($"[tribe] troop training deferred for '{status.ActiveVillage}' because its village tribe is unknown.");
            return "Troop training deferred until the active village tribe can be detected.";
        }

        var requests = BuildTroopTrainingRequests(_config, status.Tribe);
        Notify($"[troops:verbose]loaded {requests.Count} building request(s) from config.");
        Notify($"[troops:verbose]requests={string.Join(" | ", requests.Select(item => $"{item.BuildingName}:enabled={item.IsEnabled}:troop='{item.TroopType}':limit='{item.MaxQueueMode}':mode='{item.AmountMode}':keep={item.KeepResourcesPercent}%:runMode='{item.RunMode}':timed={item.TimedMinMinutes}-{item.TimedMaxMinutes}m:minRes={item.MinimumResourcesPercent}%:check=[w={item.CheckWood},c={item.CheckClay},i={item.CheckIron},crop={item.CheckCrop}]"))}.");
        var enabledRequests = requests
            .Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.TroopType))
            .ToList();
        Notify($"[troops:verbose]queue scan limited to {enabledRequests.Count} enabled building(s).");

        var fallbackCooldownSeconds = ResolveTroopTrainingFallbackCooldownSeconds(_config.TroopTrainingFallbackCooldownSeconds);
        var liveSnapshot = await ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(cancellationToken);
        var mergedEarlySnapshot = MergeTroopTrainingResourceSnapshot(status.ActiveVillage, liveSnapshot, status);
        var currentResources = mergedEarlySnapshot.Resources;
        var currentProductionByHour = mergedEarlySnapshot.ProductionByHour;
        var currentCapacities = mergedEarlySnapshot.Capacities;
        if (currentResources.Values.All(value => value <= 0))
        {
            Notify("[troops:verbose]early snapshot was empty, falling back to village status resources.");
            currentResources = ParseVillageResources(status.Resources);
            currentProductionByHour = mergedEarlySnapshot.ProductionByHour;
            currentCapacities = mergedEarlySnapshot.Capacities;
        }

        var requestsToScan = new List<TroopTrainingRequest>();
        TroopTrainingAttemptOutcome? shortestWaitOutcome = null;
        foreach (var request in enabledRequests)
        {
            if (!string.Equals(request.RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase))
            {
                requestsToScan.Add(request);
                continue;
            }

            if (MeetsMinimumResourcePercentThreshold(currentResources, currentCapacities, request))
            {
                Notify($"[troops:verbose]early % resource gate passed for {request.BuildingName} ({request.MinimumResourcesPercent}%).");
                requestsToScan.Add(request);
                continue;
            }

            var waitOutcome = BuildResourcePercentThresholdWaitOutcome(
                request.BuildingName,
                request.TroopType,
                request,
                currentResources,
                currentProductionByHour,
                currentCapacities,
                fallbackCooldownSeconds,
                $"Build troops: {request.BuildingName} waiting for {request.TroopType} resources threshold");
            Notify($"[troops:verbose]early % resource gate blocked {request.BuildingName}. wait={waitOutcome.WaitSeconds?.ToString() ?? "null"}.");
            if (waitOutcome.WaitSeconds is > 0
                && (shortestWaitOutcome is null || waitOutcome.WaitSeconds.Value < shortestWaitOutcome.WaitSeconds))
            {
                shortestWaitOutcome = waitOutcome;
            }
        }

        var queueStatuses = new List<TroopTrainingQueueStatus>();
        foreach (var request in requestsToScan)
        {
            var queueStatus = await ReadTroopTrainingQueueStatusAsync(status.Buildings, request.BuildingType, cancellationToken);
            queueStatuses.Add(queueStatus);
            SaveTroopTrainingQueueSnapshot(status, queueStatus);
        }

        var candidates = requestsToScan
            .Select(item =>
            {
                var queueStatus = queueStatuses.FirstOrDefault(entry => entry.BuildingType == item.BuildingType)
                    ?? new TroopTrainingQueueStatus(item.BuildingType, item.BuildingName, false, null, [], null, "Building not found");
                var queueRemainingSeconds = Math.Max(0, queueStatus.RemainingSeconds ?? 0);
                return new TroopTrainingCandidate(
                    item,
                    queueStatus,
                    queueRemainingSeconds,
                    TroopTrainingCalculator.TryParseTroopTrainingQueueLimitSeconds(item.MaxQueueMode));
            })
            .Where(item => item.QueueStatus.Exists)
            .OrderBy(item => item.QueueRemainingSeconds)
            .ToList();
        Notify($"[troops:verbose]queue statuses={string.Join(" | ", queueStatuses.Select(item => $"{item.BuildingName}:exists={item.Exists}:slot={(item.SlotId?.ToString() ?? "null")}:remaining='{item.RemainingText}'"))}.");

        if (candidates.Count > 0)
        {
            Notify($"[troops:verbose]candidates={string.Join(" | ", candidates.Select(item => $"{item.Request.BuildingName}:{item.Request.TroopType}:queue={(item.QueueRemainingSeconds > 0 ? TravianParsing.FormatDuration(item.QueueRemainingSeconds) : "Ready")}:limit={(item.QueueLimitSeconds is > 0 ? TravianParsing.FormatDuration(item.QueueLimitSeconds.Value) : "NoLimit")}:mode={item.Request.AmountMode}:keep={item.Request.KeepResourcesPercent}%:runMode={item.Request.RunMode}:timed={item.Request.TimedMinMinutes}-{item.Request.TimedMaxMinutes}m:minRes={item.Request.MinimumResourcesPercent}%:check=[w={item.Request.CheckWood},c={item.Request.CheckClay},i={item.Request.CheckIron},crop={item.Request.CheckCrop}]"))}.");
        }

        if (candidates.Count <= 0)
        {
            if (shortestWaitOutcome is not null)
            {
                Notify($"[troops] no candidates needed navigation — deferring, shortest wait {shortestWaitOutcome.WaitSeconds}s");
                return shortestWaitOutcome.Message;
            }

            Notify("[troops:verbose]no enabled existing candidates after filtering.");
            return $"Build troops: no enabled troop building is available in this village. queue_wait_seconds={fallbackCooldownSeconds}";
        }

        foreach (var candidate in candidates)
        {
            Notify($"[troops:verbose]evaluating {candidate.Request.BuildingName} for troop '{candidate.Request.TroopType}'.");
            if (candidate.QueueLimitSeconds is int limitSeconds
                && candidate.QueueRemainingSeconds > limitSeconds)
            {
                Notify($"[troops] skipped {candidate.Request.BuildingName} — queue {TravianParsing.FormatDuration(candidate.QueueRemainingSeconds)} exceeds limit {TravianParsing.FormatDuration(limitSeconds)}");
                var queueLimitWaitSeconds = Math.Max(1, candidate.QueueRemainingSeconds - limitSeconds);
                var queueLimitOutcome = new TroopTrainingAttemptOutcome(
                    false,
                    $"Build troops: {candidate.Request.BuildingName} queue exceeds limit. queue_wait_seconds={queueLimitWaitSeconds}",
                    queueLimitWaitSeconds);
                if (shortestWaitOutcome is null || queueLimitWaitSeconds < shortestWaitOutcome.WaitSeconds)
                {
                    shortestWaitOutcome = queueLimitOutcome;
                }

                continue;
            }

            var outcome = await TryTrainTroopsAtBuildingAsync(status, candidate, fallbackCooldownSeconds, cancellationToken);
            Notify($"[troops] {candidate.Request.BuildingName} result — success={outcome.Success}, wait={outcome.WaitSeconds?.ToString() ?? "null"}s, msg='{outcome.Message}'");
            if (outcome.Success)
            {
                return outcome.Message;
            }

            if (outcome.WaitSeconds is > 0
                && (shortestWaitOutcome is null || outcome.WaitSeconds.Value < shortestWaitOutcome.WaitSeconds))
            {
                shortestWaitOutcome = outcome;
            }
        }

        if (shortestWaitOutcome is not null)
        {
            Notify($"[troops] deferring — shortest wait {shortestWaitOutcome.WaitSeconds}s");
            return shortestWaitOutcome.Message;
        }

        return $"Build troops: no eligible troop could be trained this run. queue_wait_seconds={fallbackCooldownSeconds}";
    }


    private static IReadOnlyList<TroopTrainingRequest> BuildTroopTrainingRequests(BotOptions options, string? tribe)
    {
        return
        [
            new TroopTrainingRequest(
                TroopTrainingBuildingType.Barracks,
                "Barracks",
                options.TroopTrainingBarracksEnabled,
                ResolveConfiguredTroopType(options.TroopTrainingBarracksTroopType, tribe, TroopTrainingBuildingType.Barracks),
                options.TroopTrainingBarracksMaxQueueHours,
                options.TroopTrainingBarracksAmountMode,
                options.TroopTrainingBarracksKeepResourcesPercent,
                options.TroopTrainingBarracksRunMode,
                options.TroopTrainingBarracksMinimumTroops,
                options.TroopTrainingBarracksMinimumResourcesPercent,
                options.TroopTrainingBarracksTimedMinMinutes,
                options.TroopTrainingBarracksTimedMaxMinutes,
                options.TroopTrainingBarracksCheckWood,
                options.TroopTrainingBarracksCheckClay,
                options.TroopTrainingBarracksCheckIron,
                options.TroopTrainingBarracksCheckCrop),
            new TroopTrainingRequest(
                TroopTrainingBuildingType.Stable,
                "Stable",
                options.TroopTrainingStableEnabled,
                ResolveConfiguredTroopType(options.TroopTrainingStableTroopType, tribe, TroopTrainingBuildingType.Stable),
                options.TroopTrainingStableMaxQueueHours,
                options.TroopTrainingStableAmountMode,
                options.TroopTrainingStableKeepResourcesPercent,
                options.TroopTrainingStableRunMode,
                options.TroopTrainingStableMinimumTroops,
                options.TroopTrainingStableMinimumResourcesPercent,
                options.TroopTrainingStableTimedMinMinutes,
                options.TroopTrainingStableTimedMaxMinutes,
                options.TroopTrainingStableCheckWood,
                options.TroopTrainingStableCheckClay,
                options.TroopTrainingStableCheckIron,
                options.TroopTrainingStableCheckCrop),
            new TroopTrainingRequest(
                TroopTrainingBuildingType.Workshop,
                "Workshop",
                options.TroopTrainingWorkshopEnabled,
                ResolveConfiguredTroopType(options.TroopTrainingWorkshopTroopType, tribe, TroopTrainingBuildingType.Workshop),
                options.TroopTrainingWorkshopMaxQueueHours,
                options.TroopTrainingWorkshopAmountMode,
                options.TroopTrainingWorkshopKeepResourcesPercent,
                options.TroopTrainingWorkshopRunMode,
                options.TroopTrainingWorkshopMinimumTroops,
                options.TroopTrainingWorkshopMinimumResourcesPercent,
                options.TroopTrainingWorkshopTimedMinMinutes,
                options.TroopTrainingWorkshopTimedMaxMinutes,
                options.TroopTrainingWorkshopCheckWood,
                options.TroopTrainingWorkshopCheckClay,
                options.TroopTrainingWorkshopCheckIron,
                options.TroopTrainingWorkshopCheckCrop),
        ];
    }

    private static string ResolveConfiguredTroopType(string? configuredTroopType, string? tribe, TroopTrainingBuildingType buildingType)
    {
        if (!string.IsNullOrWhiteSpace(configuredTroopType))
        {
            return configuredTroopType;
        }

        return TroopCatalog.ResolveTroopTypesForTribe(tribe, buildingType).FirstOrDefault() ?? string.Empty;
    }

    private async Task<TroopTrainingQueueStatus> ReadTroopTrainingQueueStatusAsync(
        IReadOnlyList<Building> buildings,
        TroopTrainingBuildingType buildingType,
        CancellationToken cancellationToken)
    {
        var building = ResolveTroopTrainingBuilding(buildings, buildingType);
        var buildingName = ResolveTroopTrainingBuildingName(buildingType);
        if (building is null || building.SlotId is not > 0)
        {
            Notify($"[troops:verbose] queue scan:{buildingName} not found in current village.");
            return new TroopTrainingQueueStatus(buildingType, buildingName, false, null, [], null, "Building not found");
        }

        Notify($"[troops:verbose] queue scan:navigating to {buildingName} slot {building.SlotId.Value}.");
        await GotoAsync(Paths.BuildBySlot(building.SlotId.Value), cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var queueItems = await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = TroopTrainingCalculator.ResolveTroopTrainingQueueRemainingSeconds(queueItems);
        Notify($"[troops:verbose] queue scan:{buildingName} queue items={queueItems.Count}, maxRemaining={(remainingSeconds > 0 ? TravianParsing.FormatDuration(remainingSeconds) : "Ready")}.");
        return new TroopTrainingQueueStatus(
            buildingType,
            buildingName,
            true,
            building.SlotId,
            queueItems,
            remainingSeconds > 0 ? remainingSeconds : null,
            remainingSeconds > 0 ? TravianParsing.FormatDuration(remainingSeconds) : "Ready",
            // Anchor the finish to server time so the dashboard timer ticks against the real clock and the
            // queue stays the source of truth across reads (the in-building queue can't just vanish).
            remainingSeconds > 0 ? TimerSnapshot.FromRemaining(remainingSeconds, _serverTimeUtc) : null);
    }

    private async Task<TroopTrainingAttemptOutcome> TryTrainTroopsAtBuildingAsync(
        VillageStatus status,
        TroopTrainingCandidate candidate,
        int fallbackCooldownSeconds,
        CancellationToken cancellationToken)
    {
        var troopUnitId = TroopCatalog.ResolveTravianUnitId(status.Tribe, candidate.Request.TroopType);
        // Tribe-relative slot (1..10) — this is what the training form keys its amount inputs by (t1..t10),
        // unlike the global unit id used everywhere else. Both are used below: the slot to find the input
        // directly, the unit id to find the troop's row by its icon when the slot name differs.
        var troopSlot = TroopCatalog.ResolveTroopIndex(candidate.Request.TroopType);
        Notify($"[troops:verbose]resolved ids for '{candidate.Request.TroopType}' in tribe '{status.Tribe}' => unit={(troopUnitId?.ToString() ?? "null")}, slot={(troopSlot?.ToString() ?? "null")}.");
        if (troopUnitId is null
            || !TroopCatalog.IsTroopTypeAllowedForBuilding(status.Tribe, candidate.Request.TroopType, candidate.Request.BuildingType))
        {
            Notify($"[troops:verbose]troop '{candidate.Request.TroopType}' is invalid for {candidate.Request.BuildingName}.");
            return new TroopTrainingAttemptOutcome(false, $"Skip {candidate.Request.BuildingName}: troop '{candidate.Request.TroopType}' is not valid for this building.");
        }

        if (candidate.QueueStatus.SlotId is not > 0)
        {
            Notify($"[troops:verbose]{candidate.Request.BuildingName} slot id missing.");
            return new TroopTrainingAttemptOutcome(false, $"Skip {candidate.Request.BuildingName}: building slot not found.");
        }

        Notify($"[troops:verbose]navigating to {candidate.Request.BuildingName} slot {candidate.QueueStatus.SlotId.Value}.");
        await GotoAsync(Paths.BuildBySlot(candidate.QueueStatus.SlotId.Value), cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        Notify($"[troops:verbose]page after navigation url='{_page.Url}'.");

        // Resolve the real amount-input name once (slot first, then the unit-id icon's row, then the legacy
        // unit-id name). Every step below targets this exact input so it works on both server variants.
        var inputName = await ResolveTroopTrainingInputNameAsync(troopUnitId.Value, troopSlot);
        Notify($"[troops:verbose]resolved training input name='{inputName ?? "null"}' for unit {troopUnitId.Value}/slot {(troopSlot?.ToString() ?? "null")}.");
        if (string.IsNullOrWhiteSpace(inputName))
        {
            return new TroopTrainingAttemptOutcome(false, $"Skip {candidate.Request.BuildingName}: training input for '{candidate.Request.TroopType}' not found on the page.");
        }

        var buildInfo = await ReadTroopUnitBuildInfoFromCurrentPageAsync(inputName, cancellationToken);
        Notify($"[troops:verbose]build info found={buildInfo.Found}, canTrain={buildInfo.CanTrain}, troopType='{buildInfo.TroopType}', costs=({buildInfo.WoodCost},{buildInfo.ClayCost},{buildInfo.IronCost},{buildInfo.CropCost}).");
        if (!buildInfo.Found || !buildInfo.CanTrain)
        {
            return new TroopTrainingAttemptOutcome(false, $"Skip {candidate.Request.BuildingName}: '{candidate.Request.TroopType}' is not trainable right now.");
        }

        var liveResourceSnapshot = await ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(cancellationToken);
        var mergedLiveResourceSnapshot = MergeTroopTrainingResourceSnapshot(status.ActiveVillage, liveResourceSnapshot, status);
        var parsedResources = mergedLiveResourceSnapshot.Resources;
        var productionByHour = mergedLiveResourceSnapshot.ProductionByHour;
        var liveCapacities = mergedLiveResourceSnapshot.Capacities;
        if (parsedResources.Values.All(value => value <= 0))
        {
            Notify("[troops:verbose]current page resources were empty, falling back to village status resources.");
            parsedResources = ParseVillageResources(status.Resources);
            productionByHour = mergedLiveResourceSnapshot.ProductionByHour;
            liveCapacities = mergedLiveResourceSnapshot.Capacities;
        }
        Notify($"[troops:verbose]live resources wood={parsedResources["wood"]}, clay={parsedResources["clay"]}, iron={parsedResources["iron"]}, crop={parsedResources["crop"]}.");

        if (_config.NpcTradeEnabled)
        {
            var npcCapacities = liveCapacities;
            if (npcCapacities.WarehouseCapacity is not > 0 || npcCapacities.GranaryCapacity is not > 0)
            {
                npcCapacities = await ReadVillageStorageCapacitiesFromCurrentPageAsync(cancellationToken);
            }

            var npcTraded = await TryNpcTradeForUnitAsync(
                troopUnitId.Value,
                candidate.Request.BuildingName,
                parsedResources,
                npcCapacities,
                status.Gold,
                cancellationToken);
            if (npcTraded)
            {
                var afterNpcSnapshot = await ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(cancellationToken);
                var mergedAfterNpc = MergeTroopTrainingResourceSnapshot(status.ActiveVillage, afterNpcSnapshot, status);
                if (!mergedAfterNpc.Resources.Values.All(value => value <= 0))
                {
                    parsedResources = mergedAfterNpc.Resources;
                    productionByHour = mergedAfterNpc.ProductionByHour;
                    liveCapacities = mergedAfterNpc.Capacities;
                    Notify($"[troops:verbose]resources after NPC trade wood={parsedResources["wood"]}, clay={parsedResources["clay"]}, iron={parsedResources["iron"]}, crop={parsedResources["crop"]}.");
                }
            }
        }

        var useMaxShortcut = string.Equals(candidate.Request.AmountMode, "maximum", StringComparison.OrdinalIgnoreCase);
        var actualTrainableAmount = TroopTrainingCalculator.CalculateTroopTrainingAmount(
            parsedResources,
            buildInfo.WoodCost,
            buildInfo.ClayCost,
            buildInfo.IronCost,
            buildInfo.CropCost,
            candidate.Request.AmountMode,
            candidate.Request.KeepResourcesPercent);
        var maximumTrainableAmount = TroopTrainingCalculator.CalculateTroopTrainingAmount(
            parsedResources,
            buildInfo.WoodCost,
            buildInfo.ClayCost,
            buildInfo.IronCost,
            buildInfo.CropCost,
            "maximum",
            0);
        Notify($"[troops:verbose]live trainable amount actual={actualTrainableAmount}, maximum={maximumTrainableAmount}, mode={candidate.Request.AmountMode}, keep={candidate.Request.KeepResourcesPercent}%.");

        if (string.Equals(candidate.Request.RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase))
        {
            var capacities = liveCapacities;
            if (capacities.WarehouseCapacity is not > 0 || capacities.GranaryCapacity is not > 0)
            {
                Notify("[troops:verbose]status capacities were empty, reading capacities from current page.");
                capacities = await ReadVillageStorageCapacitiesFromCurrentPageAsync(cancellationToken);
            }
            if (!MeetsMinimumResourcePercentThreshold(parsedResources, capacities, candidate.Request))
            {
                return BuildResourcePercentThresholdWaitOutcome(
                    candidate.Request.BuildingName,
                    buildInfo.TroopType,
                    candidate.Request,
                    parsedResources,
                    productionByHour,
                    capacities,
                    fallbackCooldownSeconds,
                    $"Build troops: {candidate.Request.BuildingName} waiting for {candidate.Request.TroopType} resources threshold");
            }
        }

        var amount = 0;
        if (useMaxShortcut)
        {
            if (maximumTrainableAmount <= 0)
            {
                return BuildTroopTrainingWaitOutcome(
                    candidate,
                    buildInfo,
                    parsedResources,
                    productionByHour,
                    TroopTrainingCalculator.CalculateTroopTrainingRequiredResources(
                        buildInfo.WoodCost,
                        buildInfo.ClayCost,
                        buildInfo.IronCost,
                        buildInfo.CropCost,
                        "maximum",
                        0,
                        1),
                    fallbackCooldownSeconds,
                    $"Build troops: {candidate.Request.BuildingName} waiting for enough resources to train {candidate.Request.TroopType}");
            }
            Notify("[troops:verbose]maximum mode selected, skipping resource amount calculation.");
        }
        else
        {
            amount = actualTrainableAmount;
            Notify($"[troops:verbose]calculated amount={amount} using mode={candidate.Request.AmountMode}, keep={candidate.Request.KeepResourcesPercent}%.");
            if (amount <= 0)
            {
                return BuildTroopTrainingWaitOutcome(
                    candidate,
                    buildInfo,
                    parsedResources,
                    productionByHour,
                    TroopTrainingCalculator.CalculateTroopTrainingRequiredResources(
                        buildInfo.WoodCost,
                        buildInfo.ClayCost,
                        buildInfo.IronCost,
                        buildInfo.CropCost,
                        candidate.Request.AmountMode,
                        candidate.Request.KeepResourcesPercent,
                        1),
                    fallbackCooldownSeconds,
                    $"Build troops: {candidate.Request.BuildingName} waiting for free resources to train {candidate.Request.TroopType}");
            }
        }

        Notify($"[troops:verbose]submitting training for unitId={troopUnitId.Value}, amount={amount}, useMaxShortcut={useMaxShortcut}.");
        var submitted = await SubmitTroopTrainingFromCurrentPageAsync(
            inputName,
            Math.Max(0, amount),
            useMaxShortcut,
            cancellationToken);
        Notify($"[troops:verbose]submit result submitted={submitted}.");
        if (!submitted)
        {
            return new TroopTrainingAttemptOutcome(false, $"Skip {candidate.Request.BuildingName}: could not submit training for '{candidate.Request.TroopType}'.");
        }

        await Task.Delay(300, cancellationToken);
        Notify($"[troops:verbose]page after submit url='{_page.Url}'.");
        var resourcesAfterSubmit = await ReadVillageResourcesFromCurrentPageAsync(cancellationToken);
        Notify($"[troops:verbose]resources after submit wood={resourcesAfterSubmit["wood"]}, clay={resourcesAfterSubmit["clay"]}, iron={resourcesAfterSubmit["iron"]}, crop={resourcesAfterSubmit["crop"]}.");
        var queueItems = await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken);
        var queueSeconds = TroopTrainingCalculator.ResolveTroopTrainingQueueRemainingSeconds(queueItems);
        var queueText = queueSeconds > 0 ? TravianParsing.FormatDuration(queueSeconds) : "Ready";
        Notify($"[troops:verbose]queue after submit items={queueItems.Count}, remaining='{queueText}'.");
        SaveTroopTrainingQueueSnapshot(status, new TroopTrainingQueueStatus(
            candidate.Request.BuildingType,
            candidate.Request.BuildingName,
            true,
            candidate.QueueStatus.SlotId,
            queueItems,
            queueSeconds > 0 ? queueSeconds : null,
            queueText,
            queueSeconds > 0 ? TimerSnapshot.FromRemaining(queueSeconds, _serverTimeUtc) : null));
        if (string.Equals(candidate.Request.RunMode, "timed", StringComparison.OrdinalIgnoreCase))
        {
            var timedWaitSeconds = ResolveTimedTrainingWaitSeconds(candidate.Request);
            Notify($"[troops] timed trigger for {candidate.Request.BuildingName}: next run in {TravianParsing.FormatDuration(timedWaitSeconds)}.");
            return new TroopTrainingAttemptOutcome(
                true,
                $"Build troops: queued {(useMaxShortcut ? "maximum" : amount.ToString())} {candidate.Request.TroopType} at {candidate.Request.BuildingName}. Queue={queueText}. Stock={BuildTroopTrainingResourceSummary(resourcesAfterSubmit)}. Timed next run in {TravianParsing.FormatDuration(timedWaitSeconds)}. queue_wait_seconds={timedWaitSeconds}",
                timedWaitSeconds);
        }

        return new TroopTrainingAttemptOutcome(
            true,
            $"Build troops: queued {(useMaxShortcut ? "maximum" : amount.ToString())} {candidate.Request.TroopType} at {candidate.Request.BuildingName}. Queue={queueText}. Stock={BuildTroopTrainingResourceSummary(resourcesAfterSubmit)}.");
    }

    private static int ResolveTimedTrainingWaitSeconds(TroopTrainingRequest request)
    {
        var minMinutes = Math.Max(1, request.TimedMinMinutes);
        var maxMinutes = Math.Max(minMinutes, request.TimedMaxMinutes);
        var selectedMinutes = Random.Shared.Next(minMinutes, maxMinutes + 1);
        return selectedMinutes * 60;
    }

    private static IReadOnlyDictionary<string, long> ParseVillageResources(IReadOnlyDictionary<string, string> resources)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            var parsed = TravianParsing.TryParseResourceValue(resources.TryGetValue(key, out var raw) ? raw : null) ?? 0;
            result[key] = Math.Max(0L, parsed);
        }

        return result;
    }

    private async Task<TroopTrainingResourceSnapshot> ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadResourceSnapshotAsync(cancellationToken);
        var rawResources = snapshot.Resources ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parsedResources = ParseVillageResources(rawResources);
        var capacities = new ResourceCapacitySnapshot(snapshot.Capacities.Warehouse, snapshot.Capacities.Granary);
        var productionByHour = snapshot.ProductionByHour ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        Notify($"[troops:verbose]live snapshot resource payload wood='{rawResources.GetValueOrDefault("wood")}', clay='{rawResources.GetValueOrDefault("clay")}', iron='{rawResources.GetValueOrDefault("iron")}', crop='{rawResources.GetValueOrDefault("crop")}'.");
        Notify($"[troops:verbose]live snapshot capacities warehouse='{snapshot.Capacities.Warehouse?.ToString() ?? "null"}', granary='{snapshot.Capacities.Granary?.ToString() ?? "null"}'.");
        return new TroopTrainingResourceSnapshot(parsedResources, capacities, productionByHour);
    }

    private TroopTrainingResourceSnapshot MergeTroopTrainingResourceSnapshot(
        string villageName,
        TroopTrainingResourceSnapshot liveSnapshot,
        VillageStatus status)
    {
        var cachedSnapshot = TryGetCachedVillageResourceSnapshot(villageName);
        var statusCapacities = ResolveVillageStorageCapacities(status);
        var mergedCapacities = TroopTrainingCalculator.MergeTroopTrainingCapacities(
            liveSnapshot.Capacities,
            statusCapacities,
            cachedSnapshot?.WarehouseCapacity,
            cachedSnapshot?.GranaryCapacity);
        var mergedProductionByHour = MergeTroopTrainingProductionByHour(
            liveSnapshot.ProductionByHour,
            ReadTroopTrainingProductionByHour(status),
            cachedSnapshot?.ProductionByHour);

        var usedStatusOrCacheCapacities =
            (liveSnapshot.Capacities.WarehouseCapacity is not > 0 && mergedCapacities.WarehouseCapacity is > 0)
            || (liveSnapshot.Capacities.GranaryCapacity is not > 0 && mergedCapacities.GranaryCapacity is > 0);
        var usedStatusOrCacheProduction = !HasAnyProduction(liveSnapshot.ProductionByHour) && HasAnyProduction(mergedProductionByHour);
        if (usedStatusOrCacheCapacities || usedStatusOrCacheProduction)
        {
            Notify(
                $"Build troops: merged resource snapshot from cached/status data (prodMerged={usedStatusOrCacheProduction}, warehouse={mergedCapacities.WarehouseCapacity?.ToString() ?? "null"}, granary={mergedCapacities.GranaryCapacity?.ToString() ?? "null"}).");
        }

        return new TroopTrainingResourceSnapshot(
            liveSnapshot.Resources,
            mergedCapacities,
            mergedProductionByHour);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadVillageResourcesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(cancellationToken);
        return snapshot.Resources;
    }

    private static ResourceCapacitySnapshot ResolveVillageStorageCapacities(VillageStatus status)
    {
        return new ResourceCapacitySnapshot(
            status.WarehouseCapacity,
            status.GranaryCapacity);
    }

    internal static IReadOnlyDictionary<string, double?> MergeTroopTrainingProductionByHour(
        IReadOnlyDictionary<string, double?> liveProductionByHour,
        IReadOnlyDictionary<string, double?> statusProductionByHour,
        IReadOnlyDictionary<string, double?>? cachedProductionByHour)
    {
        return ResourceSnapshotCalculator.MergeProductionByHour(
            ResourceSnapshotCalculator.MergeProductionByHour(liveProductionByHour, statusProductionByHour),
            cachedProductionByHour);
    }

    private async Task<ResourceCapacitySnapshot> ReadVillageStorageCapacitiesFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(cancellationToken);
        return snapshot.Capacities;
    }

    private static IReadOnlyDictionary<string, double?> ReadTroopTrainingProductionByHour(VillageStatus status)
    {
        var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            result[key] = status.ResourceStorageForecasts?
                .FirstOrDefault(item => string.Equals(item.ResourceKey, key, StringComparison.OrdinalIgnoreCase))
                ?.ProductionPerHour;
        }

        return result;
    }

    private static TroopTrainingAttemptOutcome BuildResourcePercentThresholdWaitOutcome(
        string buildingName,
        string troopType,
        TroopTrainingRequest request,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        ResourceCapacitySnapshot capacities,
        int fallbackCooldownSeconds,
        string label)
    {
        return BuildTroopTrainingWaitOutcome(
            buildingName,
            troopType,
            currentResources,
            productionByHour,
            BuildRequiredResourcesForPercentThresholdOnly(request, capacities),
            fallbackCooldownSeconds,
            label);
    }

    private static IReadOnlyDictionary<string, long> BuildRequiredResourcesForPercentThresholdOnly(
        TroopTrainingRequest request,
        ResourceCapacitySnapshot capacities)
    {
        var threshold = Math.Clamp(request.MinimumResourcesPercent, 0, 100);
        var selectedKeys = ResolveCheckedResourceKeys(request).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = selectedKeys.Contains("wood") ? ComputePercentRequirement(capacities.WarehouseCapacity, threshold) : 0,
            ["clay"] = selectedKeys.Contains("clay") ? ComputePercentRequirement(capacities.WarehouseCapacity, threshold) : 0,
            ["iron"] = selectedKeys.Contains("iron") ? ComputePercentRequirement(capacities.WarehouseCapacity, threshold) : 0,
            ["crop"] = selectedKeys.Contains("crop") ? ComputePercentRequirement(capacities.GranaryCapacity, threshold) : 0,
        };
    }

    private static long ComputePercentRequirement(long? capacity, int thresholdPercent)
    {
        if (capacity is not > 0)
        {
            return 0;
        }

        return (long)Math.Ceiling(capacity.Value * (Math.Clamp(thresholdPercent, 0, 100) / 100d));
    }

    private static TroopTrainingAttemptOutcome BuildTroopTrainingWaitOutcome(
        TroopTrainingCandidate candidate,
        TroopUnitBuildInfo buildInfo,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        IReadOnlyDictionary<string, long> requiredResources,
        int fallbackCooldownSeconds,
        string label)
    {
        var waitEstimate = BuildTroopTrainingWaitEstimate(currentResources, requiredResources, productionByHour, fallbackCooldownSeconds);

        // With a "% resources" trigger at 0% there is no threshold to wait for, so the production ETA is
        // meaningless and the task would otherwise be re-run every loop (the % gate is trivially met).
        // Re-check on the user's fallback cooldown instead so it doesn't spam.
        if (string.Equals(candidate.Request.RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase)
            && candidate.Request.MinimumResourcesPercent <= 0)
        {
            waitEstimate = waitEstimate with
            {
                WaitSeconds = Math.Max(1, fallbackCooldownSeconds),
                WaitReason = "fallback_cooldown",
            };
        }

        return BuildTroopTrainingWaitOutcome(
            candidate.Request.BuildingName,
            buildInfo.TroopType,
            currentResources,
            productionByHour,
            requiredResources,
            waitEstimate,
            label);
    }

    private static TroopTrainingAttemptOutcome BuildTroopTrainingWaitOutcome(
        string buildingName,
        string troopType,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        IReadOnlyDictionary<string, long> requiredResources,
        int fallbackCooldownSeconds,
        string label)
    {
        var waitEstimate = BuildTroopTrainingWaitEstimate(currentResources, requiredResources, productionByHour, fallbackCooldownSeconds);
        return BuildTroopTrainingWaitOutcome(
            buildingName,
            troopType,
            currentResources,
            productionByHour,
            requiredResources,
            waitEstimate,
            label);
    }

    private static TroopTrainingAttemptOutcome BuildTroopTrainingWaitOutcome(
        string buildingName,
        string troopType,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        IReadOnlyDictionary<string, long> requiredResources,
        TroopTrainingWaitEstimate waitEstimate,
        string label)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            currentResources.TryGetValue(key, out var currentValue);
            requiredResources.TryGetValue(key, out var requiredValue);
            productionByHour.TryGetValue(key, out var productionValue);
            var missing = Math.Max(0, requiredValue - currentValue);
            var waitText = productionValue > 0 && missing > 0
                ? Math.Max(1, (int)Math.Ceiling((missing / productionValue.Value) * 3600d)).ToString()
                : "?";
            var productionText = productionValue?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            parts.Add($"{key}: req={requiredValue}, cur={currentValue}, miss={missing}, prod/h={productionText}, wait_s={waitText}");
        }

        var message = $"{label} | building={buildingName} | troop={troopType} | {string.Join(" | ", parts)} | queue_wait_seconds={waitEstimate.WaitSeconds} | wait_reason={waitEstimate.WaitReason}";
        return new TroopTrainingAttemptOutcome(false, message, waitEstimate.WaitSeconds);
    }

    private static TroopTrainingWaitEstimate BuildTroopTrainingWaitEstimate(
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, long> requiredResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        int fallbackCooldownSeconds)
    {
        var hasUnknownWait = false;
        var longestFiniteWait = 0;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            requiredResources.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentValue);
            var missing = Math.Max(0, requiredValue - currentValue);
            if (missing <= 0)
            {
                continue;
            }

            productionByHour.TryGetValue(key, out var production);
            if (production > 0)
            {
                var perResourceWaitSeconds = (int)Math.Ceiling((missing / production.Value) * 3600d);
                longestFiniteWait = Math.Max(longestFiniteWait, Math.Max(1, perResourceWaitSeconds));
            }
            else
            {
                hasUnknownWait = true;
            }
        }

        var waitSeconds = TroopTrainingCalculator.EstimateTroopTrainingWaitSeconds(currentResources, requiredResources, productionByHour, fallbackCooldownSeconds);
        return new TroopTrainingWaitEstimate(
            waitSeconds,
            hasUnknownWait ? "recheck_needed" : "estimated_from_status",
            requiredResources,
            productionByHour);
    }

    private static int ResolveTroopTrainingFallbackCooldownSeconds(int configuredSeconds)
    {
        return configuredSeconds switch
        {
            10 or 30 or 60 or 120 or 300 or 600 => configuredSeconds,
            _ => 30,
        };
    }

    private static string BuildTroopTrainingResourceSummary(IReadOnlyDictionary<string, long> resources)
    {
        return $"wood={TroopTrainingCalculator.GetResource(resources, "wood")}, clay={TroopTrainingCalculator.GetResource(resources, "clay")}, iron={TroopTrainingCalculator.GetResource(resources, "iron")}, crop={TroopTrainingCalculator.GetResource(resources, "crop")}";
    }

    private static bool MeetsMinimumResourcePercentThreshold(
        IReadOnlyDictionary<string, long> resources,
        ResourceCapacitySnapshot capacities,
        TroopTrainingRequest request)
    {
        var threshold = Math.Clamp(request.MinimumResourcesPercent, 0, 100);
        if (threshold <= 0)
        {
            return true;
        }
        var selectedKeys = ResolveCheckedResourceKeys(request);
        return selectedKeys.All(key =>
        {
            var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? capacities.GranaryCapacity
                : capacities.WarehouseCapacity;
            return capacity is > 0 && HasMinimumResourcePercent(resources, key, capacity.Value, threshold);
        });
    }

    private static bool HasMinimumResourcePercent(IReadOnlyDictionary<string, long> resources, string key, long capacity, int thresholdPercent)
    {
        if (capacity <= 0)
        {
            return false;
        }

        var current = resources.TryGetValue(key, out var value) ? Math.Max(0L, value) : 0L;
        return (current * 100d / capacity) >= thresholdPercent;
    }

    private static IReadOnlyList<string> ResolveCheckedResourceKeys(TroopTrainingRequest request)
    {
        var keys = new List<string>();
        if (request.CheckWood)
        {
            keys.Add("wood");
        }

        if (request.CheckClay)
        {
            keys.Add("clay");
        }

        if (request.CheckIron)
        {
            keys.Add("iron");
        }

        if (request.CheckCrop)
        {
            keys.Add("crop");
        }

        return keys.Count > 0 ? keys : ["wood", "clay", "iron", "crop"];
    }

    private static Building? ResolveTroopTrainingBuilding(IReadOnlyList<Building> buildings, TroopTrainingBuildingType buildingType)
    {
        var expectedGid = buildingType switch
        {
            TroopTrainingBuildingType.Barracks => 19,
            TroopTrainingBuildingType.Stable => 20,
            TroopTrainingBuildingType.Workshop => 21,
            _ => 0,
        };
        var expectedName = ResolveTroopTrainingBuildingName(buildingType);
        return buildings.FirstOrDefault(item =>
            (item.Gid ?? 0) == expectedGid
            || string.Equals(item.Name, expectedName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTroopTrainingBuildingName(TroopTrainingBuildingType buildingType)
    {
        return buildingType switch
        {
            TroopTrainingBuildingType.Barracks => "Barracks",
            TroopTrainingBuildingType.Stable => "Stable",
            TroopTrainingBuildingType.Workshop => "Workshop",
            _ => "Unknown",
        };
    }

    private async Task<IReadOnlyList<BuildQueueItem>> ReadTroopTrainingQueueFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const rows = [];
              const seen = new Set();

              const fmtSeconds = (raw) => {
                const total = Math.max(0, parseInt(raw, 10) || 0);
                const h = Math.floor(total / 3600);
                const m = Math.floor((total % 3600) / 60);
                const s = total % 60;
                const pad = (n) => String(n).padStart(2, '0');
                return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
              };

              for (const row of document.querySelectorAll('table.under_progress tbody tr')) {
                const desc = normalize(row.querySelector('td.desc')?.textContent || '');
                const durCell = row.querySelector('td.dur');
                const timerEl = durCell?.querySelector('.timer');
                // Prefer the countdown text, but fall back to the timer's `value`/`data-value` (the remaining
                // seconds Travian renders client-side) so the queue still reads if the text hasn't hydrated.
                const timerValue = timerEl?.getAttribute('value') || timerEl?.getAttribute('data-value') || '';
                const duration = normalize(timerEl?.textContent || durCell?.textContent || '')
                  || (/^\d+$/.test(timerValue.trim()) ? fmtSeconds(timerValue.trim()) : '');
                if (!desc || !duration) continue;
                const key = `${desc}|${duration}`;
                if (seen.has(key)) continue;
                seen.add(key);
                rows.push({ text: desc, timeLeft: duration });
              }

              if (rows.length > 0) {
                return JSON.stringify(rows);
              }

              const containers = [
                ...document.querySelectorAll('.trainQueue, .trainingQueue, .queue, [class*="queue"], [id*="queue"], .contracts, .contract')
              ];
              const timeSelector = '.timer, .countdown, .value, [counting="down"], [id^="timer"]';

              const pushCandidate = (element) => {
                if (!element) return;
                const timer = element.querySelector(timeSelector);
                if (!timer) return;
                const text = normalize(element.textContent || '');
                const timeLeft = normalize(timer.textContent || '');
                if (!text || !timeLeft) return;
                const key = `${text}|${timeLeft}`;
                if (seen.has(key)) return;
                seen.add(key);
                rows.push({ text, timeLeft });
              };

              for (const container of containers) {
                pushCandidate(container);
                for (const row of container.querySelectorAll('tr, li, .unit, .contract, .row')) {
                  pushCandidate(row);
                }
              }

              return JSON.stringify(rows);
            }
            """);

        var items = TroopTrainingPageParser.ParseTroopTrainingQueue(rawJson);

        Notify($"Troop queue scan raw json length={rawJson?.Length ?? 0}, parsedItems={items.Count}.");

        return items;
    }

    private async Task<TroopUnitBuildInfo> ReadTroopUnitBuildInfoFromCurrentPageAsync(string inputName, CancellationToken cancellationToken)
    {
        Notify($"[troops:verbose]reading troop unit build info for input '{inputName}'.");
        var rawJson = await _page.EvaluateAsync<string>(
            """
            (inputName) => {
              const input = document.querySelector(`input[name="${inputName}"], input[id="${inputName}"]`);
              if (!input) {
                return JSON.stringify({ found: false });
              }

              const action = input.closest('.action') || input.closest('tr, li, form') || input.parentElement;
              const row = action || input.parentElement;
              const readCost = (key) => {
                const iconNode = row.querySelector(`i.${key}Big, i.${key}, .${key}Big, .${key}`);
                if (iconNode) {
                  const valueNode =
                    iconNode.closest('.inlineIcon')?.querySelector('.value')
                    || iconNode.parentElement?.querySelector('.value')
                    || iconNode.nextElementSibling;
                  const digits = (valueNode?.textContent || '').replace(/[^\d]/g, '');
                  if (digits) return Number(digits);
                }

                const nodes = Array.from(row.querySelectorAll('*'));
                for (const node of nodes) {
                  const className = (node.className || '').toString();
                  if (!className || !className.split(/\s+/).some(item => item === `${key}Big` || item === key)) continue;
                  const digits = (node.textContent || '').replace(/[^\d]/g, '');
                  if (digits) return Number(digits);
                }

                return 0;
              };

              const nameNode = row.querySelector('.tit a:last-of-type, .title, h1, h2, h3, h4, .name, .desc, .unitName');
              const name = nameNode ? (nameNode.textContent || '').replace(/\s+/g, ' ').trim() : `Troop ${inputName}`;
              const disabled = input.disabled || input.getAttribute('aria-disabled') === 'true' || input.closest('.disabled');
              const submitButton = (input.form || row.closest('form') || document).querySelector('button[type="submit"], input[type="submit"], .green, .button-container');

              return JSON.stringify({
                found: true,
                canTrain: !disabled && !!submitButton,
                troopType: name,
                woodCost: readCost('r1'),
                clayCost: readCost('r2'),
                ironCost: readCost('r3'),
                cropCost: readCost('r4')
              });
            }
            """,
            inputName);

        Notify($"[troops:verbose]raw build info payload for '{inputName}': {rawJson}");

        return TroopTrainingPageParser.ParseTroopUnitBuildInfo(rawJson);
    }

    private async Task<bool> SubmitTroopTrainingFromCurrentPageAsync(string inputName, int amount, bool useMaxShortcut, CancellationToken cancellationToken)
    {
        Notify($"[troops:verbose] submit:locating input '{inputName}'. amount={amount}, useMaxShortcut={useMaxShortcut}.");
        var input = _page.Locator($"input[name='{inputName}'], input[id='{inputName}']").First;
        if (await input.CountAsync() <= 0)
        {
            Notify($"Troop training submit skipped: input '{inputName}' not found.");
            return false;
        }

        if (useMaxShortcut)
        {
            Notify($"[troops:verbose] submit:resolving max value for '{inputName}' from action link.");
            var maxAmount = await ResolveTroopTrainingMaxAmountAsync(inputName);
            Notify($"[troops:verbose] submit:resolved max value for '{inputName}' => {(maxAmount?.ToString() ?? "null")}.");
            if (maxAmount is not > 0)
            {
                Notify($"Troop training submit skipped: max value for '{inputName}' was not found.");
                return false;
            }

            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            await input.ClickAsync();
            await input.FillAsync(maxAmount.Value.ToString());
            Notify($"[troops:verbose] submit:filled '{inputName}' with max value '{maxAmount.Value}'.");
        }
        else
        {
            var integerAmount = Math.Max(0, amount);
            if (integerAmount <= 0)
            {
                Notify($"[troops:verbose] submit:integer amount for '{inputName}' was <= 0.");
                return false;
            }

            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            await input.ClickAsync();
            await input.FillAsync(integerAmount.ToString());
            Notify($"[troops:verbose] submit:filled '{inputName}' with '{integerAmount}'.");
        }

        await Task.Delay(150, cancellationToken);
        var parsedValueRaw = await input.InputValueAsync();
        var parsedDigits = new string(parsedValueRaw.Where(char.IsDigit).ToArray());
        Notify($"[troops:verbose] submit:input '{inputName}' now has raw='{parsedValueRaw}', digits='{parsedDigits}'.");
        if (!int.TryParse(parsedDigits, out var parsedValue) || parsedValue <= 0)
        {
            Notify($"Troop training submit skipped: input '{inputName}' stayed at '{parsedValueRaw}'.");
            return false;
        }

        var form = input.Locator("xpath=ancestor::form[1]").First;
        Notify($"[troops:verbose] submit:located form for '{inputName}'.");
        var submitButton = form.Locator("button.startTraining, button.green.startTraining, button[type='submit'].startTraining, button[type='submit'].green").First;
        Notify($"[troops:verbose] submit:locating Train button for '{inputName}'.");
        if (await submitButton.CountAsync() <= 0)
        {
            Notify($"Troop training submit skipped: Train button not found for '{inputName}'.");
            return false;
        }

        var submitText = (await submitButton.InnerTextAsync()).Trim();
        Notify($"[troops:verbose] submit:Train button text='{submitText}' for '{inputName}'.");
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        await submitButton.ClickAsync();
        Notify($"[troops:verbose] submit:clicked Train button for '{inputName}' with parsedValue={parsedValue}.");
        return true;
    }

    // Resolves the training form's amount-input NAME for a troop.
    // Travian keys these inputs by the tribe-relative slot (t1..t10 on Official); the global unit id is
    // only used to find the troop's row by its icon. Tries the slot name first, then the input inside the
    // row carrying the u{unitId} icon. Returns "" if none.
    private async Task<string?> ResolveTroopTrainingInputNameAsync(int troopUnitId, int? troopSlot)
    {
        var resolved = await _page.EvaluateAsync<string>(
            """
            (payload) => {
              const pick = (el) => (el && (el.getAttribute('name') || el.id)) || '';
              const slot = payload.slot;
              const unitId = payload.unitId;

              if (slot) {
                const bySlot = document.querySelector(`input[name="t${slot}"], input[id="t${slot}"]`);
                if (bySlot) return pick(bySlot);
              }

              if (unitId) {
                const icon = document.querySelector(`img.u${unitId}, .u${unitId}Section, .u${unitId}`);
                if (icon) {
                  const row = icon.closest('.action, .innerTroopWrapper, tr, li, form') || icon.parentElement;
                  const inRow = row && row.querySelector('input[name^="t"], input[id^="t"]');
                  if (inRow) return pick(inRow);
                }
              }

              return '';
            }
            """,
            new { slot = troopSlot, unitId = troopUnitId });

        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    private async Task<int?> ResolveTroopTrainingMaxAmountAsync(string inputName)
    {
        var rawValue = await _page.EvaluateAsync<string>(
            """
            (inputName) => {
              const input = document.querySelector(`input[name="${inputName}"], input[id="${inputName}"]`);
              if (!input) {
                return "";
              }

              const action = input.closest('.action, .innerTroopWrapper, .details') || input.parentElement;
              if (!action) {
                return "";
              }

              const links = Array.from(action.querySelectorAll('a[href="#"], a'));
              for (const link of links) {
                const onclick = link.getAttribute('onclick') || '';
                // Official markup can set the max through either jQuery .val(...) or direct .value assignment.
                const onclickMatch = onclick.match(/(?:\.value\s*=\s*|\.val\(\s*)(\d+)/);
                if (onclickMatch && onclickMatch[1]) {
                  return onclickMatch[1];
                }

                const text = (link.textContent || '').trim();
                if (/^\d+$/.test(text)) {
                  return text;
                }
              }

              return "";
            }
            """,
            inputName);

        if (int.TryParse(rawValue, out var maxAmount) && maxAmount > 0)
        {
            return maxAmount;
        }

        Notify($"[troops:verbose] submit:failed to parse max value for '{inputName}' from raw='{rawValue}'.");

        return null;
    }
}
