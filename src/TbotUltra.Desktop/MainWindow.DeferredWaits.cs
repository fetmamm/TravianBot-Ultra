using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Pure evaluation helpers for "deferred wait" queue logic (troop-training and
// building/resource upgrades): given a village status snapshot and queue payload,
// decide whether resources are ready and, if not, how long to wait. Extracted
// verbatim from MainWindow.xaml.cs to keep that file focused on UI wiring; all
// members are static and stateless, so this is a pure relocation with no
// behavior change.
public partial class MainWindow
{
    private static Dictionary<string, long> ReadCurrentResourcesFromStatus(VillageStatus status)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            status.Resources.TryGetValue(key, out var raw);
            result[key] = TryParseDesktopResourceValue(raw) ?? 0;
        }

        return result;
    }

    private static Dictionary<string, double?> ReadCurrentProductionByHourFromStatus(VillageStatus status)
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

    private sealed record DeferredTroopTrainingRequest(
        string BuildingName,
        bool Enabled,
        string RunMode,
        int MinimumResourcesPercent,
        bool CheckWood,
        bool CheckClay,
        bool CheckIron,
        bool CheckCrop);

    private sealed record DeferredTroopTrainingEvaluation(
        bool Ready,
        int WaitSeconds,
        string WaitReason);

    private static IReadOnlyList<DeferredTroopTrainingRequest> BuildDeferredTroopTrainingRequests(BotOptions options)
    {
        return
        [
            new DeferredTroopTrainingRequest("Barracks", options.TroopTrainingBarracksEnabled, options.TroopTrainingBarracksRunMode, options.TroopTrainingBarracksMinimumResourcesPercent, options.TroopTrainingBarracksCheckWood, options.TroopTrainingBarracksCheckClay, options.TroopTrainingBarracksCheckIron, options.TroopTrainingBarracksCheckCrop),
            new DeferredTroopTrainingRequest("Stable", options.TroopTrainingStableEnabled, options.TroopTrainingStableRunMode, options.TroopTrainingStableMinimumResourcesPercent, options.TroopTrainingStableCheckWood, options.TroopTrainingStableCheckClay, options.TroopTrainingStableCheckIron, options.TroopTrainingStableCheckCrop),
            new DeferredTroopTrainingRequest("Workshop", options.TroopTrainingWorkshopEnabled, options.TroopTrainingWorkshopRunMode, options.TroopTrainingWorkshopMinimumResourcesPercent, options.TroopTrainingWorkshopCheckWood, options.TroopTrainingWorkshopCheckClay, options.TroopTrainingWorkshopCheckIron, options.TroopTrainingWorkshopCheckCrop),
        ];
    }

    private static DeferredTroopTrainingEvaluation EvaluateDeferredTroopTrainingWait(
        IReadOnlyList<DeferredTroopTrainingRequest> requests,
        IReadOnlyList<Building> knownBuildings,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        long warehouseCapacity,
        long granaryCapacity,
        int fallbackCooldownSeconds)
    {
        var enabledRequests = requests
            .Where(item => item.Enabled)
            .Where(item => string.Equals(item.RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase))
            .Where(item => knownBuildings.Count == 0 || knownBuildings.Any(building =>
                string.Equals(building.Name, item.BuildingName, StringComparison.OrdinalIgnoreCase)
                || (item.BuildingName == "Barracks" && (building.Gid ?? 0) == 19)
                || (item.BuildingName == "Stable" && (building.Gid ?? 0) == 20)
                || (item.BuildingName == "Workshop" && (building.Gid ?? 0) == 21)))
            .ToList();
        if (enabledRequests.Count == 0)
        {
            return new DeferredTroopTrainingEvaluation(false, fallbackCooldownSeconds, "skip_refresh");
        }

        var shortestWait = int.MaxValue;
        var waitReason = "fallback_cooldown";
        foreach (var request in enabledRequests)
        {
            var selectedKeys = ResolveDeferredTroopTrainingResourceKeys(request);
            var meetsThreshold = true;
            var requestWait = 0;
            var requestReason = "fallback_cooldown";
            foreach (var key in selectedKeys)
            {
                var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                    ? granaryCapacity
                    : warehouseCapacity;
                var thresholdPercent = Math.Clamp(request.MinimumResourcesPercent, 0, 100);
                if (thresholdPercent <= 0)
                {
                    continue;
                }

                var threshold = (long)Math.Ceiling(capacity * (thresholdPercent / 100d));
                currentResources.TryGetValue(key, out var currentValue);
                var missing = Math.Max(0L, threshold - currentValue);
                if (missing <= 0)
                {
                    continue;
                }

                meetsThreshold = false;
                productionByHour.TryGetValue(key, out var productionValue);
                if (productionValue > 0)
                {
                    var perResourceWait = Math.Max(1, (int)Math.Ceiling((missing / productionValue.Value) * 3600d));
                    requestWait = Math.Max(requestWait, perResourceWait);
                    requestReason = "estimated_from_status";
                }
                else
                {
                    requestWait = Math.Max(requestWait, fallbackCooldownSeconds);
                    requestReason = "recheck_needed";
                }
            }

            if (meetsThreshold)
            {
                return new DeferredTroopTrainingEvaluation(true, 0, "ready");
            }

            if (requestWait > 0 && requestWait < shortestWait)
            {
                shortestWait = requestWait;
                waitReason = requestReason;
            }
        }

        if (shortestWait == int.MaxValue)
        {
            shortestWait = fallbackCooldownSeconds;
        }

        return new DeferredTroopTrainingEvaluation(false, shortestWait, waitReason);
    }

    private static IReadOnlyList<string> ResolveDeferredTroopTrainingResourceKeys(DeferredTroopTrainingRequest request)
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

    private static int ResolveTroopTrainingFallbackCooldownSeconds(int configuredSeconds)
    {
        return configuredSeconds switch
        {
            10 or 30 or 60 or 120 or 300 or 600 => configuredSeconds,
            _ => 30,
        };
    }

    private static bool TryReadDeferredUpgradeRequirements(IReadOnlyDictionary<string, string> payload, out Dictionary<string, long> required)
    {
        required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        foreach (var pair in DeferredRequirementKeys)
        {
            if (!payload.TryGetValue(pair.Value, out var raw) || !long.TryParse(raw, out var value))
            {
                continue;
            }

            required[pair.Key] = value;
            found = true;
        }

        return found;
    }

    private static DeferredUpgradeEvaluation EvaluateDeferredUpgradeWait(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, long> required,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> liveProductionByHour)
    {
        var resourcesEnough = true;
        var longestFiniteWait = 0;
        var hasUnknownWait = false;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            required.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentValue);
            var missing = Math.Max(0, requiredValue - currentValue);
            if (missing <= 0)
            {
                continue;
            }

            resourcesEnough = false;
            liveProductionByHour.TryGetValue(key, out var liveProduction);
            var production = liveProduction ?? ReadStoredProductionValue(payload, key);
            if (production > 0)
            {
                var waitSeconds = (int)Math.Ceiling((missing / production.Value) * 3600d);
                longestFiniteWait = Math.Max(longestFiniteWait, Math.Max(1, waitSeconds));
                continue;
            }

            hasUnknownWait = true;
        }

        if (resourcesEnough)
        {
            return new DeferredUpgradeEvaluation(true, 0, "resources_ready");
        }

        var wait = longestFiniteWait > 0 ? longestFiniteWait : 60;
        if (hasUnknownWait)
        {
            wait = Math.Max(30, Math.Min(wait, 60));
        }

        return new DeferredUpgradeEvaluation(false, wait, hasUnknownWait ? "recheck_needed" : "estimated_from_status");
    }

    private static void WriteDeferredUpgradeRuntimeValues(
        Dictionary<string, string> payload,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        DeferredUpgradeEvaluation evaluation)
    {
        foreach (var pair in DeferredCurrentKeys)
        {
            if (currentResources.TryGetValue(pair.Key, out var current))
            {
                payload[pair.Value] = current.ToString();
            }
        }

        foreach (var pair in DeferredProductionKeys)
        {
            if (productionByHour.TryGetValue(pair.Key, out var production) && production.HasValue)
            {
                payload[pair.Value] = production.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        payload[BotOptionPayloadKeys.UpgradeWaitSeconds] = evaluation.WaitSeconds.ToString();
        payload[BotOptionPayloadKeys.UpgradeWaitReason] = evaluation.WaitReason;
    }

    private static double? ReadStoredProductionValue(IReadOnlyDictionary<string, string> payload, string resourceKey)
    {
        if (!DeferredProductionKeys.TryGetValue(resourceKey, out var key))
        {
            return null;
        }

        if (!payload.TryGetValue(key, out var raw))
        {
            return null;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? TryParseDesktopResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        return long.TryParse(cleaned, out var value) ? value : null;
    }

    private static string DescribeDeferredUpgrade(IReadOnlyDictionary<string, string> payload)
    {
        if (payload.TryGetValue(BotOptionPayloadKeys.UpgradeBlockedLabel, out var blockedLabel) && !string.IsNullOrWhiteSpace(blockedLabel))
        {
            return blockedLabel.Replace('_', ' ');
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeName, out var buildingName) && !string.IsNullOrWhiteSpace(buildingName))
        {
            return buildingName;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeName, out var resourceName) && !string.IsNullOrWhiteSpace(resourceName))
        {
            return resourceName;
        }

        return "upgrade";
    }

    private sealed record DeferredUpgradeEvaluation(bool ResourcesEnough, int WaitSeconds, string WaitReason);

    private static readonly string[] DeferredUpgradePayloadKeys =
    [
        BotOptionPayloadKeys.UpgradeBlockedLabel,
        BotOptionPayloadKeys.UpgradeRequiredWood,
        BotOptionPayloadKeys.UpgradeRequiredClay,
        BotOptionPayloadKeys.UpgradeRequiredIron,
        BotOptionPayloadKeys.UpgradeRequiredCrop,
        BotOptionPayloadKeys.UpgradeCurrentWood,
        BotOptionPayloadKeys.UpgradeCurrentClay,
        BotOptionPayloadKeys.UpgradeCurrentIron,
        BotOptionPayloadKeys.UpgradeCurrentCrop,
        BotOptionPayloadKeys.UpgradeProductionWood,
        BotOptionPayloadKeys.UpgradeProductionClay,
        BotOptionPayloadKeys.UpgradeProductionIron,
        BotOptionPayloadKeys.UpgradeProductionCrop,
        BotOptionPayloadKeys.UpgradeWaitSeconds,
        BotOptionPayloadKeys.UpgradeWaitReason,
    ];

    private static readonly IReadOnlyDictionary<string, string> DeferredRequirementKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = BotOptionPayloadKeys.UpgradeRequiredWood,
        ["clay"] = BotOptionPayloadKeys.UpgradeRequiredClay,
        ["iron"] = BotOptionPayloadKeys.UpgradeRequiredIron,
        ["crop"] = BotOptionPayloadKeys.UpgradeRequiredCrop,
    };

    private static readonly IReadOnlyDictionary<string, string> DeferredCurrentKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = BotOptionPayloadKeys.UpgradeCurrentWood,
        ["clay"] = BotOptionPayloadKeys.UpgradeCurrentClay,
        ["iron"] = BotOptionPayloadKeys.UpgradeCurrentIron,
        ["crop"] = BotOptionPayloadKeys.UpgradeCurrentCrop,
    };

    private static readonly IReadOnlyDictionary<string, string> DeferredProductionKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = BotOptionPayloadKeys.UpgradeProductionWood,
        ["clay"] = BotOptionPayloadKeys.UpgradeProductionClay,
        ["iron"] = BotOptionPayloadKeys.UpgradeProductionIron,
        ["crop"] = BotOptionPayloadKeys.UpgradeProductionCrop,
    };
}
