using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public enum StorageCapacityKind
{
    Warehouse,
    Granary,
}

public enum StorageDependencyAction
{
    Wait,
    Upgrade,
    Construct,
    Pause,
}

public sealed record StorageCapacityBlock(
    StorageCapacityKind Kind,
    long RequiredCapacity,
    long CurrentCapacity);

public sealed record StorageDependencyPlan(
    StorageDependencyAction Action,
    StorageCapacityKind Kind,
    int? SlotId,
    int? TargetLevel,
    int WaitSeconds,
    string Reason);

public static class StorageCapacityDependencyPlanner
{
    // Official Warehouse/Granary capacity per level. Level 0 is the village's base capacity and lets
    // the planner calculate the additional capacity contributed by upgrading one storage building even
    // when the village has multiple storage buildings.
    private static readonly long[] StorageCapacityByLevel =
    [
        800, 1_200, 1_700, 2_300, 3_100, 4_000, 5_000, 6_300, 7_800, 9_600,
        11_800, 14_400, 17_600, 21_400, 25_900, 31_300, 37_900, 45_700, 55_100, 66_400, 80_000,
    ];

    public static StorageCapacityBlock? ResolveBlock(
        long requiredWood,
        long requiredClay,
        long requiredIron,
        long requiredCrop,
        long? warehouseCapacity,
        long? granaryCapacity)
    {
        var requiredWarehouse = Math.Max(requiredWood, Math.Max(requiredClay, requiredIron));
        if (warehouseCapacity is > 0 && requiredWarehouse > warehouseCapacity.Value)
        {
            return new StorageCapacityBlock(
                StorageCapacityKind.Warehouse,
                requiredWarehouse,
                warehouseCapacity.Value);
        }

        if (granaryCapacity is > 0 && requiredCrop > granaryCapacity.Value)
        {
            return new StorageCapacityBlock(
                StorageCapacityKind.Granary,
                requiredCrop,
                granaryCapacity.Value);
        }

        return null;
    }

    public static StorageDependencyPlan Plan(
        StorageCapacityKind kind,
        VillageStatus status,
        IReadOnlyCollection<int> queuedConstructSlots,
        DateTimeOffset nowUtc,
        long? requiredCapacity = null,
        long? currentVillageCapacity = null)
    {
        var name = kind == StorageCapacityKind.Warehouse ? "Warehouse" : "Granary";
        var gid = kind == StorageCapacityKind.Warehouse ? 10 : 11;
        var active = (status.ActiveConstructions ?? [])
            .Where(item => item.Kind != ConstructionKind.Resource && IsStorageName(item.Name, kind))
            .OrderBy(item => item.Finish?.RemainingSecondsAt(nowUtc) ?? item.TimeLeftSeconds ?? int.MaxValue)
            .FirstOrDefault();
        if (active is not null)
        {
            var waitSeconds = active.Finish?.RemainingSecondsAt(nowUtc)
                ?? active.TimeLeftSeconds
                ?? 60;
            return new StorageDependencyPlan(
                StorageDependencyAction.Wait,
                kind,
                null,
                null,
                Math.Max(1, waitSeconds),
                $"{name} construction already active");
        }

        var matchingBuildings = status.Buildings
            .Where(building => building.SlotId is >= 19 and <= 38)
            .Where(building => building.Gid == gid || IsStorageName(building.Name, kind))
            .Where(building => (building.Level ?? 0) > 0)
            .ToList();
        var upgradeCandidate = matchingBuildings
            .Where(building => building.Level is int level && level < BuildingCatalogService.MaxLevelFor(gid))
            .OrderByDescending(building => building.Level)
            .FirstOrDefault();
        if (upgradeCandidate?.SlotId is int upgradeSlot && upgradeCandidate.Level is int currentLevel)
        {
            var targetLevel = ResolveUpgradeTargetLevel(
                currentLevel,
                currentVillageCapacity,
                requiredCapacity,
                BuildingCatalogService.MaxLevelFor(gid));
            return new StorageDependencyPlan(
                StorageDependencyAction.Upgrade,
                kind,
                upgradeSlot,
                targetLevel,
                0,
                $"upgrade {name} from level {currentLevel} to {targetLevel}");
        }

        var occupiedSlots = status.Buildings
            .Where(IsOccupiedBuildingSlot)
            .Select(building => building.SlotId!.Value)
            .Concat(queuedConstructSlots)
            .ToHashSet();
        var emptySlot = Enumerable.Range(19, 20)
            .FirstOrDefault(slotId => !occupiedSlots.Contains(slotId));
        if (emptySlot > 0)
        {
            return new StorageDependencyPlan(
                StorageDependencyAction.Construct,
                kind,
                emptySlot,
                1,
                0,
                $"construct new {name} in slot {emptySlot}");
        }

        return new StorageDependencyPlan(
            StorageDependencyAction.Pause,
            kind,
            null,
            null,
            0,
            $"no empty building slot is available for a new {name}");
    }

    internal static int ResolveUpgradeTargetLevel(
        int currentLevel,
        long? currentVillageCapacity,
        long? requiredCapacity,
        int maxLevel = 20)
    {
        var normalizedCurrentLevel = Math.Clamp(currentLevel, 0, Math.Min(maxLevel, StorageCapacityByLevel.Length - 1));
        var normalizedMaxLevel = Math.Clamp(maxLevel, normalizedCurrentLevel, StorageCapacityByLevel.Length - 1);
        var fallbackTarget = Math.Min(normalizedCurrentLevel + 1, normalizedMaxLevel);
        if (currentVillageCapacity is not > 0
            || requiredCapacity is not > 0
            || requiredCapacity.Value <= currentVillageCapacity.Value)
        {
            return fallbackTarget;
        }

        var currentBuildingCapacity = StorageCapacityByLevel[normalizedCurrentLevel];
        for (var targetLevel = fallbackTarget; targetLevel <= normalizedMaxLevel; targetLevel++)
        {
            var addedCapacity = StorageCapacityByLevel[targetLevel] - currentBuildingCapacity;
            if (currentVillageCapacity.Value + addedCapacity >= requiredCapacity.Value)
            {
                return targetLevel;
            }
        }

        return normalizedMaxLevel;
    }

    internal static long CapacityAtLevel(int level)
    {
        return StorageCapacityByLevel[Math.Clamp(level, 0, StorageCapacityByLevel.Length - 1)];
    }

    public static Dictionary<string, string> BuildDependencyPayload(
        StorageDependencyPlan plan,
        Guid parentId,
        IReadOnlyDictionary<string, string> parentPayload)
    {
        Dictionary<string, string> payload;
        var name = plan.Kind == StorageCapacityKind.Warehouse ? "Warehouse" : "Granary";
        var gid = plan.Kind == StorageCapacityKind.Warehouse ? 10 : 11;
        if (plan.Action == StorageDependencyAction.Upgrade)
        {
            payload = new BuildingUpgradePayload(plan.SlotId!.Value, plan.TargetLevel, name).ToDictionary();
        }
        else if (plan.Action == StorageDependencyAction.Construct)
        {
            payload = new BuildingConstructPayload(plan.SlotId!.Value, gid, name).ToDictionary();
        }
        else
        {
            throw new InvalidOperationException($"Cannot build dependency payload for action {plan.Action}.");
        }

        CopyIfPresent(parentPayload, payload, TbotUltra.Core.Configuration.BotOptionPayloadKeys.TargetVillageName);
        CopyIfPresent(parentPayload, payload, TbotUltra.Core.Configuration.BotOptionPayloadKeys.TargetVillageUrl);
        CopyIfPresent(parentPayload, payload, TbotUltra.Core.Configuration.BotOptionPayloadKeys.NpcTradeEnabled);
        payload[TbotUltra.Core.Configuration.BotOptionPayloadKeys.StorageDependencyParentId] = parentId.ToString();
        payload[TbotUltra.Core.Configuration.BotOptionPayloadKeys.StorageDependencyKind] = plan.Kind.ToString().ToLowerInvariant();
        return payload;
    }

    private static bool IsOccupiedBuildingSlot(Building building)
    {
        if (building.SlotId is not (>= 19 and <= 38))
        {
            return false;
        }

        if ((building.Level ?? 0) > 0 || (building.Gid ?? 0) > 0)
        {
            return true;
        }

        var name = building.Name?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "Empty", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "g0", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageName(string? name, StorageCapacityKind kind)
    {
        var normalized = string.Join(
                " ",
                (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();
        return kind == StorageCapacityKind.Warehouse
            ? normalized == "warehouse"
            : normalized is "granary" or "granary / silo" or "silo";
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }
}
