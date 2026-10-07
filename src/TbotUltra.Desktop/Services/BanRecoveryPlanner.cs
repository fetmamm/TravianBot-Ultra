using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

internal sealed record BanRecoveryVillageIssue(string VillageKey, string VillageName, string Message);

internal sealed record BanRecoveryPlan(
    IReadOnlyList<QueueItemCreateRequest> Requests,
    IReadOnlyList<BanRecoveryVillageIssue> Issues,
    IReadOnlySet<string> AffectedVillageKeys,
    int LostLevels,
    int ExistingConstructionItemsToReplace)
{
    public bool HasWork => Requests.Count > 0;
}

internal static class BanRecoveryPlanner
{
    public static BanRecoveryPlan Plan(
        IReadOnlyDictionary<string, VillageStatus> baseline,
        IReadOnlyDictionary<string, VillageStatus> current,
        IReadOnlyCollection<string> failedVillageKeys,
        IReadOnlyList<QueueItem> queueItems)
    {
        var requests = new List<QueueItemCreateRequest>();
        var issues = new List<BanRecoveryVillageIssue>();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lostLevels = 0;

        foreach (var failedKey in failedVillageKeys)
        {
            var name = baseline.TryGetValue(failedKey, out var failedBaseline) ? failedBaseline.ActiveVillage : failedKey;
            issues.Add(new BanRecoveryVillageIssue(failedKey, name, "The recovery scan failed; this village was not changed."));
        }

        foreach (var pair in current.Where(pair => !baseline.ContainsKey(pair.Key)))
        {
            issues.Add(new BanRecoveryVillageIssue(
                pair.Key,
                pair.Value.ActiveVillage,
                "No pre-ban snapshot exists; this village was not changed."));
        }

        foreach (var pair in baseline.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var villageKey = pair.Key;
            var before = pair.Value;
            var baselineFieldCount = before.ResourceFields
                .Where(field => field.SlotId is >= 1 and <= 18)
                .Select(field => field.SlotId)
                .Distinct()
                .Count();
            if (baselineFieldCount < 18)
            {
                issues.Add(new BanRecoveryVillageIssue(villageKey, before.ActiveVillage,
                    $"The pre-ban snapshot contains {baselineFieldCount}/18 resource fields; only known fields can be restored."));
            }
            var baselineBuildingCount = before.Buildings
                .Where(building => building.SlotId is >= 19 and <= 43)
                .Select(building => building.SlotId)
                .Distinct()
                .Count();
            if (baselineBuildingCount < 22)
            {
                issues.Add(new BanRecoveryVillageIssue(villageKey, before.ActiveVillage,
                    $"The pre-ban snapshot contains {baselineBuildingCount}/22 building slots; only known buildings can be restored."));
            }
            if (!current.TryGetValue(villageKey, out var after))
            {
                if (!failedVillageKeys.Contains(villageKey, StringComparer.OrdinalIgnoreCase))
                {
                    issues.Add(new BanRecoveryVillageIssue(villageKey, before.ActiveVillage, "No verified post-ban village snapshot was available."));
                }
                continue;
            }

            var villageRequests = new List<(int Order, int Slot, QueueItemCreateRequest Request)>();
            var targetVillage = after.Villages.FirstOrDefault(v =>
                v.CoordX == after.ActiveVillageCoordX && v.CoordY == after.ActiveVillageCoordY)
                ?? before.Villages.FirstOrDefault(v =>
                    v.CoordX == before.ActiveVillageCoordX && v.CoordY == before.ActiveVillageCoordY);

            var activeBySlot = (after.ActiveConstructions ?? [])
                .Where(item => item.SlotId.HasValue)
                .GroupBy(item => item.SlotId!.Value)
                .ToDictionary(group => group.Key, group => group.Max(item => item.Level ?? 0));

            foreach (var field in before.ResourceFields.Where(field => field.SlotId is >= 1 and <= 18 && field.Level > 0))
            {
                var currentField = after.ResourceFields.FirstOrDefault(candidate => candidate.SlotId == field.SlotId);
                if (currentField?.Level is not int currentLevel) continue;
                var effectiveLevel = Math.Max(currentLevel, activeBySlot.GetValueOrDefault(field.SlotId!.Value));
                if (effectiveLevel >= field.Level!.Value) continue;

                lostLevels += field.Level.Value - effectiveLevel;
                affected.Add(villageKey);
                var payload = new ResourceUpgradePayload(field.SlotId.Value, field.Level.Value, field.Name).ToDictionary();
                StampVillage(payload, villageKey, after.ActiveVillage, targetVillage?.Url);
                villageRequests.Add((2, field.SlotId.Value, new QueueItemCreateRequest("upgrade_resource_to_level", payload, 0, 3)));
            }

            var afterBySlot = after.Buildings.Where(building => building.SlotId.HasValue)
                .ToDictionary(building => building.SlotId!.Value);
            var requirementNames = RequiredBuildingNames(before.Buildings);
            foreach (var building in before.Buildings.Where(building => building.SlotId > 18 && building.Level > 0 && building.Gid > 0))
            {
                var slot = building.SlotId!.Value;
                afterBySlot.TryGetValue(slot, out var currentBuilding);
                if (currentBuilding is not null
                    && (currentBuilding.Level ?? 0) <= 0
                    && !currentBuilding.Gid.HasValue)
                {
                    currentBuilding = null;
                }
                if (currentBuilding is not null && currentBuilding.Gid != building.Gid)
                {
                    issues.Add(new BanRecoveryVillageIssue(villageKey, before.ActiveVillage,
                        $"Slot {slot} now contains '{currentBuilding.Name}' instead of '{building.Name}'; it was skipped."));
                    continue;
                }

                var effectiveLevel = Math.Max(currentBuilding?.Level ?? 0, activeBySlot.GetValueOrDefault(slot));
                if (effectiveLevel >= building.Level!.Value) continue;

                lostLevels += building.Level.Value - effectiveLevel;
                affected.Add(villageKey);
                var order = BuildingOrder(building, requirementNames);
                if (currentBuilding is null)
                {
                    var constructPayload = new BuildingConstructPayload(
                        slot,
                        building.Gid!.Value,
                        building.Name,
                        building.Level.Value).ToDictionary();
                    constructPayload[BotOptionPayloadKeys.BuildingConstructAllowSlotFallback] = "false";
                    StampVillage(constructPayload, villageKey, after.ActiveVillage, targetVillage?.Url);
                    villageRequests.Add((order, slot, new QueueItemCreateRequest("construct_building", constructPayload, 0, 3)));
                    effectiveLevel = building.Level.Value;
                }

                if (building.Level.Value > effectiveLevel)
                {
                    var upgradePayload = new BuildingUpgradePayload(slot, building.Level.Value, building.Name).ToDictionary();
                    StampVillage(upgradePayload, villageKey, after.ActiveVillage, targetVillage?.Url);
                    villageRequests.Add((order, slot, new QueueItemCreateRequest("upgrade_building_to_level", upgradePayload, 0, 3)));
                }
            }

            requests.AddRange(villageRequests.OrderBy(item => item.Order).ThenBy(item => item.Slot).Select(item => item.Request));
        }

        var replaced = queueItems.Count(item => item.Group == QueueGroup.Construction
            && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
        return new BanRecoveryPlan(requests, issues, affected, lostLevels, replaced);
    }

    private static HashSet<string> RequiredBuildingNames(IReadOnlyList<Building> buildings)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var building in buildings.Where(item => item.Gid.HasValue))
        {
            foreach (var requirement in BuildingCatalogService.RequirementsFor(building.Gid!.Value))
            {
                result.Add(requirement.Name);
            }
        }
        return result;
    }

    private static int BuildingOrder(Building building, IReadOnlySet<string> requirementNames)
    {
        if (building.Gid is 10 or 11 or 38 or 39 || requirementNames.Contains(building.Name)) return 0;
        if (building.Gid == 15) return 1;
        return 3;
    }

    private static void StampVillage(Dictionary<string, string> payload, string key, string name, string? url)
    {
        payload[BotOptionPayloadKeys.TargetVillageKey] = key;
        payload[BotOptionPayloadKeys.TargetVillageName] = name;
        payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByBanRecovery;
        if (!string.IsNullOrWhiteSpace(url)) payload[BotOptionPayloadKeys.TargetVillageUrl] = url;
    }
}
