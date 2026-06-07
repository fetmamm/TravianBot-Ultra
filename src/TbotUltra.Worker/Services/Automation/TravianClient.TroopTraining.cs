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
        bool CheckWood,
        bool CheckClay,
        bool CheckIron,
        bool CheckCrop);

    private sealed record TroopTrainingCandidate(
        TroopTrainingRequest Request,
        TroopTrainingQueueStatus QueueStatus,
        int QueueRemainingSeconds,
        int? QueueLimitSeconds);

    private sealed record TroopUnitBuildInfo(
        bool Found,
        bool CanTrain,
        string TroopType,
        int WoodCost,
        int ClayCost,
        int IronCost,
        int CropCost);

    internal sealed record ResourceCapacitySnapshot(
        long? WarehouseCapacity,
        long? GranaryCapacity);

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
        await EnsureLoggedInAsync();

        IReadOnlyList<Building> buildings = knownBuildings ?? (await ReadBuildingsStatusAsync(cancellationToken)).Buildings;
        Notify($"[troops:verbose] queue scan:using {buildings.Count} known building(s).");
        var statuses = new List<TroopTrainingQueueStatus>();
        foreach (var buildingType in new[]
                 {
                     TroopTrainingBuildingType.Barracks,
                     TroopTrainingBuildingType.Stable,
                     TroopTrainingBuildingType.Workshop,
                 })
        {
            Notify($"[troops:verbose] queue scan:reading {buildingType}.");
            var queueStatus = await ReadTroopTrainingQueueStatusAsync(buildings, buildingType, cancellationToken);
            statuses.Add(queueStatus);
            Notify($"[troops:verbose] queue scan:{queueStatus.BuildingName} exists={queueStatus.Exists}, remaining={(queueStatus.RemainingSeconds is > 0 ? queueStatus.RemainingText : "Ready")}.");
        }

        return statuses;
    }

    public async Task<string> BuildTroopsAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();
        Notify("[troops:verbose]loading village status.");

        var status = await ReadVillageStatusAsync(cancellationToken);
        Notify($"[troops:verbose]activeVillage='{status.ActiveVillage}', tribe='{status.Tribe}', resources={string.Join(", ", status.Resources.Select(pair => $"{pair.Key}={pair.Value}"))}.");
        var requests = BuildTroopTrainingRequests(_config, status.Tribe);
        Notify($"[troops:verbose]loaded {requests.Count} building request(s) from config.");
        Notify($"[troops:verbose]requests={string.Join(" | ", requests.Select(item => $"{item.BuildingName}:enabled={item.IsEnabled}:troop='{item.TroopType}':limit='{item.MaxQueueMode}':mode='{item.AmountMode}':keep={item.KeepResourcesPercent}%:runMode='{item.RunMode}':minTroops={item.MinimumTroops}:minRes={item.MinimumResourcesPercent}%:check=[w={item.CheckWood},c={item.CheckClay},i={item.CheckIron},crop={item.CheckCrop}]"))}.");
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

            var requiredResources = BuildRequiredResourcesForPercentThresholdOnly(request, currentCapacities);
            var waitOutcome = BuildTroopTrainingWaitOutcome(
                request.BuildingName,
                request.TroopType,
                currentResources,
                currentProductionByHour,
                requiredResources,
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
                    TryParseTroopTrainingQueueLimitSeconds(item.MaxQueueMode));
            })
            .Where(item => item.QueueStatus.Exists)
            .OrderBy(item => item.QueueRemainingSeconds)
            .ToList();
        Notify($"[troops:verbose]queue statuses={string.Join(" | ", queueStatuses.Select(item => $"{item.BuildingName}:exists={item.Exists}:slot={(item.SlotId?.ToString() ?? "null")}:remaining='{item.RemainingText}'"))}.");

        if (candidates.Count > 0)
        {
            Notify($"[troops:verbose]candidates={string.Join(" | ", candidates.Select(item => $"{item.Request.BuildingName}:{item.Request.TroopType}:queue={(item.QueueRemainingSeconds > 0 ? FormatDuration(item.QueueRemainingSeconds) : "Ready")}:limit={(item.QueueLimitSeconds is > 0 ? FormatDuration(item.QueueLimitSeconds.Value) : "NoLimit")}:mode={item.Request.AmountMode}:keep={item.Request.KeepResourcesPercent}%:runMode={item.Request.RunMode}:minTroops={item.Request.MinimumTroops}:minRes={item.Request.MinimumResourcesPercent}%:check=[w={item.Request.CheckWood},c={item.Request.CheckClay},i={item.Request.CheckIron},crop={item.Request.CheckCrop}]"))}.");
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
                Notify($"[troops] skipped {candidate.Request.BuildingName} — queue {FormatDuration(candidate.QueueRemainingSeconds)} exceeds limit {FormatDuration(limitSeconds)}");
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

    internal static int? TryParseTroopTrainingQueueLimitSeconds(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || string.Equals(mode, "no_limit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(mode.Trim(), out var hours) && hours > 0
            ? hours * 3600
            : null;
    }

    internal static int ResolveTroopTrainingQueueRemainingSeconds(IReadOnlyList<BuildQueueItem> items)
    {
        return items
            .Select(item => ParseDurationToSeconds(item.TimeLeft))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Sum();
    }

    internal static int CalculateTroopTrainingAmount(
        IReadOnlyDictionary<string, long> resources,
        int woodCost,
        int clayCost,
        int ironCost,
        int cropCost,
        string amountMode,
        int keepResourcesPercent)
    {
        if (woodCost <= 0 || clayCost <= 0 || ironCost <= 0 || cropCost <= 0)
        {
            return 0;
        }

        var normalizedMode = NormalizeTroopTrainingAmountMode(amountMode);
        var reserveFactor = normalizedMode == "keep_resources"
            ? Math.Clamp(keepResourcesPercent, 0, 95) / 100d
            : 0d;

        var woodAvailable = ComputeAvailableResource(resources, "wood", reserveFactor);
        var clayAvailable = ComputeAvailableResource(resources, "clay", reserveFactor);
        var ironAvailable = ComputeAvailableResource(resources, "iron", reserveFactor);
        var cropAvailable = ComputeAvailableResource(resources, "crop", reserveFactor);

        var trainable = new[]
        {
            woodAvailable / woodCost,
            clayAvailable / clayCost,
            ironAvailable / ironCost,
            cropAvailable / cropCost,
        }.Min();

        return trainable >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Max(0L, trainable);
    }

    internal static string NormalizeTroopTrainingAmountMode(string? amountMode)
    {
        return string.Equals(amountMode, "keep_resources", StringComparison.OrdinalIgnoreCase)
            ? "keep_resources"
            : "maximum";
    }

    internal static IReadOnlyDictionary<string, long> CalculateTroopTrainingRequiredResources(
        int woodCost,
        int clayCost,
        int ironCost,
        int cropCost,
        string amountMode,
        int keepResourcesPercent,
        int troopCount)
    {
        var count = Math.Max(1, troopCount);
        return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = CalculateRequiredCurrentResource(woodCost, count, amountMode, keepResourcesPercent),
            ["clay"] = CalculateRequiredCurrentResource(clayCost, count, amountMode, keepResourcesPercent),
            ["iron"] = CalculateRequiredCurrentResource(ironCost, count, amountMode, keepResourcesPercent),
            ["crop"] = CalculateRequiredCurrentResource(cropCost, count, amountMode, keepResourcesPercent),
        };
    }

    internal static int EstimateTroopTrainingWaitSeconds(
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, long> requiredResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        int fallbackCooldownSeconds = 60)
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
                var waitSeconds = (int)Math.Ceiling((missing / production.Value) * 3600d);
                longestFiniteWait = Math.Max(longestFiniteWait, Math.Max(1, waitSeconds));
                continue;
            }

            hasUnknownWait = true;
        }

        if (longestFiniteWait <= 0)
        {
            return fallbackCooldownSeconds;
        }

        return hasUnknownWait
            ? Math.Max(Math.Min(fallbackCooldownSeconds, 60), Math.Min(longestFiniteWait, 60))
            : longestFiniteWait;
    }

    private static long GetResource(IReadOnlyDictionary<string, long> resources, string key)
    {
        return resources.TryGetValue(key, out var value) ? Math.Max(0L, value) : 0L;
    }

    private static long ComputeAvailableResource(IReadOnlyDictionary<string, long> resources, string key, double reserveFactor)
    {
        var current = GetResource(resources, key);
        if (current <= 0)
        {
            return 0;
        }

        var reserve = reserveFactor <= 0
            ? 0L
            : (long)Math.Floor(current * reserveFactor);
        return Math.Max(0L, current - reserve);
    }

    private static long CalculateRequiredCurrentResource(int unitCost, int troopCount, string amountMode, int keepResourcesPercent)
    {
        if (unitCost <= 0 || troopCount <= 0)
        {
            return 0;
        }

        var totalCost = (long)unitCost * troopCount;
        if (!string.Equals(NormalizeTroopTrainingAmountMode(amountMode), "keep_resources", StringComparison.OrdinalIgnoreCase))
        {
            return totalCost;
        }

        var reserveFactor = Math.Clamp(keepResourcesPercent, 0, 95) / 100d;
        if (reserveFactor <= 0)
        {
            return totalCost;
        }

        var denominator = Math.Max(0.05d, 1d - reserveFactor);
        long low = totalCost;
        long high = Math.Max(low, (long)Math.Ceiling(totalCost / denominator) + 2);
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            var available = ComputeAvailableResource(
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["value"] = mid },
                "value",
                reserveFactor);
            if (available >= totalCost)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
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
        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while reading the {buildingName} queue.", cancellationToken);
        await EnsureLoggedInAsync();

        var queueItems = await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = ResolveTroopTrainingQueueRemainingSeconds(queueItems);
        Notify($"[troops:verbose] queue scan:{buildingName} queue items={queueItems.Count}, maxRemaining={(remainingSeconds > 0 ? FormatDuration(remainingSeconds) : "Ready")}.");
        return new TroopTrainingQueueStatus(
            buildingType,
            buildingName,
            true,
            building.SlotId,
            queueItems,
            remainingSeconds > 0 ? remainingSeconds : null,
            remainingSeconds > 0 ? FormatDuration(remainingSeconds) : "Ready");
    }

    private async Task<TroopTrainingAttemptOutcome> TryTrainTroopsAtBuildingAsync(
        VillageStatus status,
        TroopTrainingCandidate candidate,
        int fallbackCooldownSeconds,
        CancellationToken cancellationToken)
    {
        var troopUnitId = TroopCatalog.ResolveTravianUnitId(status.Tribe, candidate.Request.TroopType);
        Notify($"[troops:verbose]resolved unit id for '{candidate.Request.TroopType}' in tribe '{status.Tribe}' => {(troopUnitId?.ToString() ?? "null")}.");
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
        await PauseForManualStepIfVisibleAsync($"Manual verification appeared while opening the {candidate.Request.BuildingName}.", cancellationToken);
        await EnsureLoggedInAsync();
        Notify($"[troops:verbose]page after navigation url='{_page.Url}'.");

        var buildInfo = await ReadTroopUnitBuildInfoFromCurrentPageAsync(troopUnitId.Value, cancellationToken);
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
        var actualTrainableAmount = CalculateTroopTrainingAmount(
            parsedResources,
            buildInfo.WoodCost,
            buildInfo.ClayCost,
            buildInfo.IronCost,
            buildInfo.CropCost,
            candidate.Request.AmountMode,
            candidate.Request.KeepResourcesPercent);
        var maximumTrainableAmount = CalculateTroopTrainingAmount(
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
                return BuildTroopTrainingWaitOutcome(
                    candidate,
                    buildInfo,
                    parsedResources,
                    productionByHour,
                    BuildRequiredResourcesForResourcePercentMode(candidate, buildInfo, capacities),
                    fallbackCooldownSeconds,
                    $"Build troops: {candidate.Request.BuildingName} waiting for {candidate.Request.TroopType} resources threshold");
            }
        }

        if (string.Equals(candidate.Request.RunMode, "min_troops", StringComparison.OrdinalIgnoreCase)
            && actualTrainableAmount < candidate.Request.MinimumTroops)
        {
            return BuildTroopTrainingWaitOutcome(
                candidate,
                buildInfo,
                parsedResources,
                productionByHour,
                CalculateTroopTrainingRequiredResources(
                    buildInfo.WoodCost,
                    buildInfo.ClayCost,
                    buildInfo.IronCost,
                    buildInfo.CropCost,
                    candidate.Request.AmountMode,
                    candidate.Request.KeepResourcesPercent,
                    candidate.Request.MinimumTroops),
                fallbackCooldownSeconds,
                $"Build troops: {candidate.Request.BuildingName} waiting to reach minimum troops for {candidate.Request.TroopType}");
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
                    CalculateTroopTrainingRequiredResources(
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
                    CalculateTroopTrainingRequiredResources(
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
            troopUnitId.Value,
            Math.Max(0, amount),
            useMaxShortcut,
            cancellationToken);
        Notify($"[troops:verbose]submit result submitted={submitted}.");
        if (!submitted)
        {
            return new TroopTrainingAttemptOutcome(false, $"Skip {candidate.Request.BuildingName}: could not submit training for '{candidate.Request.TroopType}'.");
        }

        await Task.Delay(300, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting troop training.", cancellationToken);
        Notify($"[troops:verbose]page after submit url='{_page.Url}'.");
        var resourcesAfterSubmit = await ReadVillageResourcesFromCurrentPageAsync(cancellationToken);
        Notify($"[troops:verbose]resources after submit wood={resourcesAfterSubmit["wood"]}, clay={resourcesAfterSubmit["clay"]}, iron={resourcesAfterSubmit["iron"]}, crop={resourcesAfterSubmit["crop"]}.");
        var queueItems = await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken);
        var queueSeconds = ResolveTroopTrainingQueueRemainingSeconds(queueItems);
        var queueText = queueSeconds > 0 ? FormatDuration(queueSeconds) : "Ready";
        Notify($"[troops:verbose]queue after submit items={queueItems.Count}, remaining='{queueText}'.");
        return new TroopTrainingAttemptOutcome(
            true,
            $"Build troops: queued {(useMaxShortcut ? "maximum" : amount.ToString())} {candidate.Request.TroopType} at {candidate.Request.BuildingName}. Queue={queueText}. Stock={BuildTroopTrainingResourceSummary(resourcesAfterSubmit)}.");
    }

    private static IReadOnlyDictionary<string, long> ParseVillageResources(IReadOnlyDictionary<string, string> resources)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            var parsed = TryParseResourceValue(resources.TryGetValue(key, out var raw) ? raw : null) ?? 0;
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
        var mergedCapacities = MergeTroopTrainingCapacities(
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

    internal static ResourceCapacitySnapshot MergeTroopTrainingCapacities(
        ResourceCapacitySnapshot liveCapacities,
        ResourceCapacitySnapshot statusCapacities,
        long? cachedWarehouseCapacity,
        long? cachedGranaryCapacity)
    {
        return new ResourceCapacitySnapshot(
            liveCapacities.WarehouseCapacity is > 0
                ? liveCapacities.WarehouseCapacity
                : statusCapacities.WarehouseCapacity is > 0
                    ? statusCapacities.WarehouseCapacity
                    : cachedWarehouseCapacity,
            liveCapacities.GranaryCapacity is > 0
                ? liveCapacities.GranaryCapacity
                : statusCapacities.GranaryCapacity is > 0
                    ? statusCapacities.GranaryCapacity
                    : cachedGranaryCapacity);
    }

    internal static IReadOnlyDictionary<string, double?> MergeTroopTrainingProductionByHour(
        IReadOnlyDictionary<string, double?> liveProductionByHour,
        IReadOnlyDictionary<string, double?> statusProductionByHour,
        IReadOnlyDictionary<string, double?>? cachedProductionByHour)
    {
        return MergeProductionByHour(
            MergeProductionByHour(liveProductionByHour, statusProductionByHour),
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

    private static IReadOnlyDictionary<string, long> BuildRequiredResourcesForResourcePercentMode(
        TroopTrainingCandidate candidate,
        TroopUnitBuildInfo buildInfo,
        ResourceCapacitySnapshot capacities)
    {
        var troopRequirement = CalculateTroopTrainingRequiredResources(
            buildInfo.WoodCost,
            buildInfo.ClayCost,
            buildInfo.IronCost,
            buildInfo.CropCost,
            candidate.Request.AmountMode,
            candidate.Request.KeepResourcesPercent,
            1);
        var threshold = Math.Clamp(candidate.Request.MinimumResourcesPercent, 0, 100);
        var selectedKeys = ResolveCheckedResourceKeys(candidate.Request).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = selectedKeys.Contains("wood") ? Math.Max(
                troopRequirement.TryGetValue("wood", out var troopWood) ? troopWood : 0,
                ComputePercentRequirement(capacities.WarehouseCapacity, threshold)) : 0,
            ["clay"] = selectedKeys.Contains("clay") ? Math.Max(
                troopRequirement.TryGetValue("clay", out var troopClay) ? troopClay : 0,
                ComputePercentRequirement(capacities.WarehouseCapacity, threshold)) : 0,
            ["iron"] = selectedKeys.Contains("iron") ? Math.Max(
                troopRequirement.TryGetValue("iron", out var troopIron) ? troopIron : 0,
                ComputePercentRequirement(capacities.WarehouseCapacity, threshold)) : 0,
            ["crop"] = selectedKeys.Contains("crop") ? Math.Max(
                troopRequirement.TryGetValue("crop", out var troopCrop) ? troopCrop : 0,
                ComputePercentRequirement(capacities.GranaryCapacity, threshold)) : 0,
        };
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

        var waitSeconds = EstimateTroopTrainingWaitSeconds(currentResources, requiredResources, productionByHour, fallbackCooldownSeconds);
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
        return $"wood={GetResource(resources, "wood")}, clay={GetResource(resources, "clay")}, iron={GetResource(resources, "iron")}, crop={GetResource(resources, "crop")}";
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
        if (capacities.WarehouseCapacity is not > 0 || capacities.GranaryCapacity is not > 0)
        {
            return false;
        }

        var selectedKeys = ResolveCheckedResourceKeys(request);
        foreach (var key in selectedKeys)
        {
            var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                ? capacities.GranaryCapacity.Value
                : capacities.WarehouseCapacity.Value;
            if (!HasMinimumResourcePercent(resources, key, capacity, threshold))
            {
                return false;
            }
        }

        return true;
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
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading the troop training queue.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const rows = [];
              const seen = new Set();

              for (const row of document.querySelectorAll('table.under_progress tbody tr')) {
                const desc = normalize(row.querySelector('td.desc')?.textContent || '');
                const durCell = row.querySelector('td.dur');
                const duration = normalize(durCell?.querySelector('.timer')?.textContent || durCell?.textContent || '');
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

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? []
            : JsonSerializer.Deserialize<List<BuildQueueJs>>(rawJson) ?? [];

        Notify($"Troop queue scan raw json length={rawJson?.Length ?? 0}, parsedItems={raw.Count}.");

        return raw
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new BuildQueueItem(item.Text!, item.TimeLeft))
            .ToList();
    }

    private async Task<TroopUnitBuildInfo> ReadTroopUnitBuildInfoFromCurrentPageAsync(int troopIndex, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading troop build costs.", cancellationToken);
        Notify($"[troops:verbose]reading troop unit build info for t{troopIndex}.");
        var rawJson = await _page.EvaluateAsync<string>(
            """
            (troopIndex) => {
              const input = document.querySelector(`input[name="t${troopIndex}"], input[id="t${troopIndex}"]`);
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
              const name = nameNode ? (nameNode.textContent || '').replace(/\s+/g, ' ').trim() : `Troop ${troopIndex}`;
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
            troopIndex);

        Notify($"[troops:verbose]raw build info payload for t{troopIndex}: {rawJson}");

        try
        {
            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;
            return new TroopUnitBuildInfo(
                root.TryGetProperty("found", out var found) && found.GetBoolean(),
                root.TryGetProperty("canTrain", out var canTrain) && canTrain.GetBoolean(),
                root.TryGetProperty("troopType", out var troopType) ? troopType.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("woodCost", out var woodCost) ? woodCost.GetInt32() : 0,
                root.TryGetProperty("clayCost", out var clayCost) ? clayCost.GetInt32() : 0,
                root.TryGetProperty("ironCost", out var ironCost) ? ironCost.GetInt32() : 0,
                root.TryGetProperty("cropCost", out var cropCost) ? cropCost.GetInt32() : 0);
        }
        catch
        {
            return new TroopUnitBuildInfo(false, false, string.Empty, 0, 0, 0, 0);
        }
    }

    private async Task<bool> SubmitTroopTrainingFromCurrentPageAsync(int troopIndex, int amount, bool useMaxShortcut, CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while submitting troop training.", cancellationToken);
        Notify($"[troops:verbose] submit:locating input t{troopIndex}. amount={amount}, useMaxShortcut={useMaxShortcut}.");
        var input = _page.Locator($"input[name='t{troopIndex}'], input[id='t{troopIndex}']").First;
        if (await input.CountAsync() <= 0)
        {
            Notify($"Troop training submit skipped: input t{troopIndex} not found.");
            return false;
        }

        var action = input.Locator("xpath=ancestor::*[contains(@class,'action')][1]").First;
        Notify($"[troops:verbose] submit:located action container for t{troopIndex}.");
        if (useMaxShortcut)
        {
            Notify($"[troops:verbose] submit:resolving max value for t{troopIndex} from action link.");
            var maxAmount = await ResolveTroopTrainingMaxAmountAsync(troopIndex);
            Notify($"[troops:verbose] submit:resolved max value for t{troopIndex} => {(maxAmount?.ToString() ?? "null")}.");
            if (maxAmount is not > 0)
            {
                Notify($"Troop training submit skipped: max value for t{troopIndex} was not found.");
                return false;
            }

            await _page.EvaluateAsync(
                """
                (payload) => {
                  const input = document.querySelector(`input[name="t${payload.troopIndex}"], input[id="t${payload.troopIndex}"]`);
                  if (!input) {
                    return false;
                  }

                  input.value = String(payload.maxAmount);
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
                """,
                new { troopIndex, maxAmount = maxAmount.Value });
            Notify($"[troops:verbose] submit:set t{troopIndex} directly to max value '{maxAmount.Value}'.");
            Notify($"[troops:verbose] submit: clicked max shortcut value {maxAmount.Value}");
        }
        else
        {
            var integerAmount = Math.Max(0, amount);
            if (integerAmount <= 0)
            {
                Notify($"[troops:verbose] submit:integer amount for t{troopIndex} was <= 0.");
                return false;
            }

            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            await input.ClickAsync();
            await input.FillAsync(integerAmount.ToString());
            Notify($"[troops:verbose] submit:filled t{troopIndex} with '{integerAmount}'.");
        }

        await Task.Delay(150, cancellationToken);
        var parsedValueRaw = await input.InputValueAsync();
        var parsedDigits = new string(parsedValueRaw.Where(char.IsDigit).ToArray());
        Notify($"[troops:verbose] submit:input t{troopIndex} now has raw='{parsedValueRaw}', digits='{parsedDigits}'.");
        if (!int.TryParse(parsedDigits, out var parsedValue) || parsedValue <= 0)
        {
            Notify($"Troop training submit skipped: input t{troopIndex} stayed at '{parsedValueRaw}'.");
            return false;
        }

        var form = input.Locator("xpath=ancestor::form[1]").First;
        Notify($"[troops:verbose] submit:located form for t{troopIndex}.");
        var submitButton = form.Locator("button.startTraining, button.green.startTraining, button[type='submit'].startTraining, button[type='submit'].green").First;
        Notify($"[troops:verbose] submit:locating Train button for t{troopIndex}.");
        if (await submitButton.CountAsync() <= 0)
        {
            Notify($"Troop training submit skipped: Train button not found for t{troopIndex}.");
            return false;
        }

        var submitText = (await submitButton.InnerTextAsync()).Trim();
        Notify($"[troops:verbose] submit:Train button text='{submitText}' for t{troopIndex}.");
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        await submitButton.ClickAsync();
        Notify($"[troops:verbose] submit:clicked Train button for t{troopIndex} with parsedValue={parsedValue}.");
        Notify("[troops:verbose] submit: Train button clicked");
        return true;
    }

    private async Task<int?> ResolveTroopTrainingMaxAmountAsync(int troopIndex)
    {
        var rawValue = await _page.EvaluateAsync<string>(
            """
            (troopIndex) => {
              const input = document.querySelector(`input[name="t${troopIndex}"], input[id="t${troopIndex}"]`);
              if (!input) {
                return "";
              }

              const action = input.closest('.action') || input.parentElement;
              if (!action) {
                return "";
              }

              const links = Array.from(action.querySelectorAll('a[href="#"], a'));
              for (const link of links) {
                const onclick = link.getAttribute('onclick') || '';
                const onclickMatch = onclick.match(new RegExp(`t${troopIndex}\\.value=(\\d+)`));
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
            troopIndex);

        if (int.TryParse(rawValue, out var maxAmount) && maxAmount > 0)
        {
            return maxAmount;
        }

        Notify($"[troops:verbose] submit:failed to parse max value for t{troopIndex} from raw='{rawValue}'.");

        return null;
    }
}
