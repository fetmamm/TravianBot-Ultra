using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

internal enum ConstructionRequirementRepairStepKind
{
    Enqueue,
    Promote,
}

internal sealed record ConstructionRequirementRepairStep(
    ConstructionRequirementRepairStepKind Kind,
    Guid? ExistingQueueItemId,
    string TaskName,
    Dictionary<string, string> Payload,
    string DisplayName,
    string Reason,
    string RequirementText);

internal sealed record ConstructionRequirementRepairPlan(
    IReadOnlyList<ConstructionRequirementRepairStep> Steps,
    IReadOnlyList<string> WaitReasons,
    IReadOnlyList<string> Blockers)
{
    public bool HasSteps => Steps.Count > 0;
    public bool HasBlockers => Blockers.Count > 0;
    public string Detail
    {
        get
        {
            var parts = Steps.Select(step => step.Reason)
                .Concat(WaitReasons)
                .Concat(Blockers)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return parts.Count == 0 ? "no repair action" : string.Join("; ", parts);
        }
    }
}

internal static class ConstructionRequirementRepairPlanner
{
    private static readonly HashSet<int> WallGids = [31, 32, 33, 42, 43];
    private static readonly HashSet<int> DuplicateAllowedGids = [10, 11, 23, 38, 39];
    private static readonly HashSet<int> FixedSlotGids = [16, 31, 32, 33, 42, 43];
    private const int RallyPointSlotId = 39;
    private const int WallSlotId = 40;

    public static ConstructionRequirementRepairPlan Plan(
        QueueItem parent,
        VillageStatus status,
        IReadOnlyList<QueueItem> sameVillageQueueItems,
        DateTimeOffset now)
    {
        if (!string.Equals(parent.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(parent.Payload, out var construct)
            || construct is null)
        {
            return new ConstructionRequirementRepairPlan([], [], ["parent item is not a construct_building task"]);
        }

        var requirements = BuildingCatalogService.RequirementsFor(construct.Gid);
        if (requirements.Count == 0)
        {
            return new ConstructionRequirementRepairPlan([], [], []);
        }

        var state = RepairState.From(status, sameVillageQueueItems, now);
        var steps = new List<ConstructionRequirementRepairStep>();
        var waitReasons = new List<string>();
        var blockers = new List<string>();
        var stack = new HashSet<int>();

        foreach (var requirement in requirements)
        {
            EnsureRequirement(requirement, status, sameVillageQueueItems, state, steps, waitReasons, blockers, stack, now);
        }

        return new ConstructionRequirementRepairPlan(steps, waitReasons, blockers);
    }

    private static void EnsureRequirement(
        BuildingRequirementEntry requirement,
        VillageStatus status,
        IReadOnlyList<QueueItem> queueItems,
        RepairState state,
        List<ConstructionRequirementRepairStep> steps,
        List<string> waitReasons,
        List<string> blockers,
        HashSet<int> stack,
        DateTimeOffset now)
    {
        var gid = BuildingCatalogService.GidForName(requirement.Name);
        if (gid is null)
        {
            blockers.Add($"cannot resolve gid for requirement {FormatRequirement(requirement)}");
            return;
        }

        if (gid is >= 1 and <= 4)
        {
            EnsureResourceRequirement(requirement, gid.Value, queueItems, state, steps, blockers);
            return;
        }

        EnsureBuildingRequirement(
            requirement.Name,
            gid.Value,
            requirement.Level,
            status,
            queueItems,
            state,
            steps,
            waitReasons,
            blockers,
            stack,
            now);
    }

    private static void EnsureBuildingRequirement(
        string requestedName,
        int gid,
        int targetLevel,
        VillageStatus status,
        IReadOnlyList<QueueItem> queueItems,
        RepairState state,
        List<ConstructionRequirementRepairStep> steps,
        List<string> waitReasons,
        List<string> blockers,
        HashSet<int> stack,
        DateTimeOffset now)
    {
        var name = ResolveCatalogName(gid, requestedName, status.Tribe);
        targetLevel = Math.Clamp(targetLevel, 1, BuildingCatalogService.MaxLevelFor(gid));

        var existing = state.FindExistingBuilding(gid, name);
        var existingLevel = existing?.Level ?? 0;
        if (existingLevel >= targetLevel)
        {
            return;
        }

        var active = state.FindActiveBuilding(gid, name, now);
        if (active is not null)
        {
            var activeLevel = active.Level ?? 0;
            if (activeLevel >= targetLevel)
            {
                state.ApplyBuilding(active.SlotId ?? existing?.SlotId ?? 0, gid, name, activeLevel);
                waitReasons.Add($"{name} {targetLevel}+ already active in Travian");
                return;
            }

            if (active.SlotId is int activeSlot && activeLevel > existingLevel)
            {
                state.ApplyBuilding(activeSlot, gid, name, activeLevel);
                existing = new PlannedBuilding(activeSlot, gid, name, activeLevel);
                existingLevel = activeLevel;
                waitReasons.Add($"{name} active in Travian before repair can continue");
            }
            else if (existing is null)
            {
                waitReasons.Add($"{name} active in Travian, waiting before creating repair");
                return;
            }
        }

        if (existing is null)
        {
            if (!stack.Add(gid))
            {
                blockers.Add($"cycle while planning {name}");
                return;
            }

            foreach (var prerequisite in BuildingCatalogService.RequirementsFor(gid))
            {
                EnsureRequirement(prerequisite, status, queueItems, state, steps, waitReasons, blockers, stack, now);
            }

            stack.Remove(gid);

            if (blockers.Count > 0)
            {
                return;
            }

            var queuedConstruct = FindPendingConstruct(queueItems, gid, name, state);
            if (queuedConstruct is not null
                && BuildingConstructPayload.TryFromDictionary(queuedConstruct.Payload, out var construct)
                && construct is not null)
            {
                var payload = new Dictionary<string, string>(queuedConstruct.Payload, StringComparer.OrdinalIgnoreCase);
                steps.Add(new ConstructionRequirementRepairStep(
                    ConstructionRequirementRepairStepKind.Promote,
                    queuedConstruct.Id,
                    queuedConstruct.TaskName,
                    payload,
                    $"Promote queued {name} construct",
                    $"promote queued {name} construct before blocked task",
                    $"{name} {targetLevel}+"));
                state.ApplyBuilding(construct.SlotId, gid, name, 1);
                existing = new PlannedBuilding(construct.SlotId, gid, name, 1);
                existingLevel = 1;
            }
            else
            {
                if (!CanConstructNew(gid, name, status, state, out var reason))
                {
                    blockers.Add(reason);
                    return;
                }

                var slotId = ResolveConstructSlot(gid, state);
                if (slotId is null)
                {
                    blockers.Add($"no free building slot is available for {name}");
                    return;
                }

                var payload = new BuildingConstructPayload(slotId.Value, gid, name).ToDictionary();
                steps.Add(new ConstructionRequirementRepairStep(
                    ConstructionRequirementRepairStepKind.Enqueue,
                    null,
                    "construct_building",
                    payload,
                    $"Construct {name} to level 1 (slot {slotId.Value})",
                    $"construct missing prerequisite {name} in slot {slotId.Value}",
                    $"{name} {targetLevel}+"));
                state.ApplyBuilding(slotId.Value, gid, name, 1);
                existing = new PlannedBuilding(slotId.Value, gid, name, 1);
                existingLevel = 1;
            }
        }

        if (existingLevel >= targetLevel)
        {
            return;
        }

        var queuedUpgrade = FindPendingUpgrade(queueItems, existing.SlotId, gid, name, targetLevel);
        if (queuedUpgrade is not null)
        {
            var payload = new Dictionary<string, string>(queuedUpgrade.Payload, StringComparer.OrdinalIgnoreCase);
            steps.Add(new ConstructionRequirementRepairStep(
                ConstructionRequirementRepairStepKind.Promote,
                queuedUpgrade.Id,
                queuedUpgrade.TaskName,
                payload,
                $"Promote queued {name} upgrade",
                $"promote queued {name} upgrade to level {targetLevel}",
                $"{name} {targetLevel}+"));
            state.ApplyBuilding(existing.SlotId, gid, name, targetLevel);
            return;
        }

        var upgradePayload = new BuildingUpgradePayload(existing.SlotId, targetLevel, name).ToDictionary();
        steps.Add(new ConstructionRequirementRepairStep(
            ConstructionRequirementRepairStepKind.Enqueue,
            null,
            "upgrade_building_to_level",
            upgradePayload,
            $"Upgrade {name} to level {targetLevel} (slot {existing.SlotId})",
            $"upgrade prerequisite {name} to level {targetLevel}",
            $"{name} {targetLevel}+"));
        state.ApplyBuilding(existing.SlotId, gid, name, targetLevel);
    }

    private static void EnsureResourceRequirement(
        BuildingRequirementEntry requirement,
        int gid,
        IReadOnlyList<QueueItem> queueItems,
        RepairState state,
        List<ConstructionRequirementRepairStep> steps,
        List<string> blockers)
    {
        if (state.ResourceLevelFor(requirement.Name) >= requirement.Level)
        {
            return;
        }

        var queuedUpgrade = FindPendingResourceUpgrade(queueItems, requirement.Name, requirement.Level);
        if (queuedUpgrade is not null)
        {
            var queuedPayload = new Dictionary<string, string>(queuedUpgrade.Payload, StringComparer.OrdinalIgnoreCase);
            steps.Add(new ConstructionRequirementRepairStep(
                ConstructionRequirementRepairStepKind.Promote,
                queuedUpgrade.Id,
                queuedUpgrade.TaskName,
                queuedPayload,
                $"Promote queued {requirement.Name} upgrade",
                $"promote queued {FormatRequirement(requirement)} upgrade",
                FormatRequirement(requirement)));
            state.ApplyResource(requirement.Name, requirement.Level);
            return;
        }

        var field = state.FindResourceField(requirement.Name);
        if (field is null)
        {
            blockers.Add($"no resource field found for {FormatRequirement(requirement)}");
            return;
        }

        var fieldName = string.IsNullOrWhiteSpace(field.Name) ? requirement.Name : field.Name;
        var payload = new ResourceUpgradePayload(field.SlotId, requirement.Level, fieldName).ToDictionary();
        steps.Add(new ConstructionRequirementRepairStep(
            ConstructionRequirementRepairStepKind.Enqueue,
            null,
            "upgrade_resource_to_level",
            payload,
            $"Upgrade {fieldName} slot {field.SlotId} to level {requirement.Level}",
            $"upgrade prerequisite {FormatRequirement(requirement)}",
            FormatRequirement(requirement)));
        state.ApplyResource(requirement.Name, requirement.Level);
    }

    private static QueueItem? FindPendingConstruct(
        IReadOnlyList<QueueItem> queueItems,
        int gid,
        string name,
        RepairState state)
    {
        return queueItems
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(item => BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
                && payload is not null
                && (payload.Gid == gid || NameMatches(payload.Name, name))
                && state.CanReuseQueuedConstructSlot(payload.SlotId, gid));
    }

    private static QueueItem? FindPendingUpgrade(
        IReadOnlyList<QueueItem> queueItems,
        int slotId,
        int gid,
        string name,
        int targetLevel)
    {
        return queueItems
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(item => BuildingUpgradePayload.TryFromDictionary(item.Payload, out var payload)
                && payload is not null
                && payload.SlotId == slotId
                && NameMatches(payload.Name, name)
                && ResolveQueuedUpgradeTarget(item, gid, payload) >= targetLevel);
    }

    private static QueueItem? FindPendingResourceUpgrade(
        IReadOnlyList<QueueItem> queueItems,
        string requirementName,
        int targetLevel)
    {
        var requirementCategory = ResourceCategory(requirementName);
        if (requirementCategory is null)
        {
            return null;
        }

        return queueItems
            .Where(item => item.Status == QueueStatus.Pending)
            .FirstOrDefault(item =>
            {
                if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
                {
                    var queuedTarget = TryGetIntPayloadValue(item.Payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                        ?? TryGetIntPayloadValue(item.Payload, BotOptionPayloadKeys.TargetLevel);
                    return queuedTarget.HasValue && queuedTarget.Value >= targetLevel;
                }

                if (!string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                    || !ResourceUpgradePayload.TryFromDictionary(item.Payload, out var payload)
                    || payload is null)
                {
                    return false;
                }

                var category = ResourceCategory(payload.Name);
                return string.Equals(category, requirementCategory, StringComparison.Ordinal)
                    && payload.TargetLevel >= targetLevel;
            });
    }

    private static int ResolveQueuedUpgradeTarget(QueueItem item, int gid, BuildingUpgradePayload payload)
    {
        if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            return BuildingCatalogService.MaxLevelFor(gid);
        }

        return payload.TargetLevel ?? 0;
    }

    private static int? ResolveConstructSlot(int gid, RepairState state)
    {
        if (gid == 16)
        {
            return state.IsSlotFreeForNewConstruct(RallyPointSlotId, gid) ? RallyPointSlotId : null;
        }

        if (WallGids.Contains(gid))
        {
            return state.IsSlotFreeForNewConstruct(WallSlotId, gid) ? WallSlotId : null;
        }

        return Enumerable.Range(19, 20)
            .FirstOrDefault(slotId => state.IsSlotFreeForNewConstruct(slotId, gid)) is int slot && slot > 0
                ? slot
                : null;
    }

    private static bool CanConstructNew(
        int gid,
        string name,
        VillageStatus status,
        RepairState state,
        out string reason)
    {
        reason = string.Empty;
        if ((gid is 29 or 30) && status.IsCapital == true)
        {
            reason = $"{name} cannot be built in the capital";
            return false;
        }

        if (gid == 26 && (state.HasGid(25) || state.HasGid(44)))
        {
            reason = "Palace conflicts with Residence or Command Center";
            return false;
        }

        if (gid == 25 && (state.HasGid(26) || state.HasGid(44)))
        {
            reason = "Residence conflicts with Palace or Command Center";
            return false;
        }

        if (gid == 44 && (state.HasGid(25) || state.HasGid(26)))
        {
            reason = "Command Center conflicts with Palace or Residence";
            return false;
        }

        if (BuildingCatalogService.DuplicateRequiredExistingLevelFor(gid) is int duplicateRequiredLevel
            && state.HasGidPresence(gid)
            && state.HighestLevelForGid(gid) < duplicateRequiredLevel)
        {
            reason = $"{name} can only be duplicated after an existing one reaches level {duplicateRequiredLevel}";
            return false;
        }

        if (BuildingCatalogService.IsSingleInstance(gid)
            && !DuplicateAllowedGids.Contains(gid)
            && !FixedSlotGids.Contains(gid)
            && state.HasGid(gid))
        {
            reason = $"{name} already exists or is queued in this village";
            return false;
        }

        if (WallGids.Contains(gid) && state.HasAnyWall())
        {
            reason = "Wall already exists or is queued in this village";
            return false;
        }

        return true;
    }

    private static string ResolveCatalogName(int gid, string fallback, string tribe)
    {
        var entry = BuildingCatalogService.GetFullCatalog(tribe)
            .FirstOrDefault(item => item.Gid == gid);
        return string.IsNullOrWhiteSpace(entry?.Name) ? fallback : entry.Name;
    }

    private static int? TryGetIntPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;
    }

    private static string FormatRequirement(BuildingRequirementEntry requirement)
        => $"{requirement.Name} {requirement.Level}+";

    private static bool NameMatches(string? left, string right)
        => string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && left.Contains(right, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && right.Contains(left, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("'", string.Empty).Replace("’", string.Empty).ToLowerInvariant();

    private static string? ResourceCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (name.Contains("Clay", StringComparison.OrdinalIgnoreCase)) return "clay";
        if (name.Contains("Iron", StringComparison.OrdinalIgnoreCase)) return "iron";
        if (name.Contains("Crop", StringComparison.OrdinalIgnoreCase)) return "crop";
        return null;
    }

    private sealed record PlannedBuilding(int SlotId, int Gid, string Name, int Level);

    private sealed record PlannedResourceField(int SlotId, string Name, string FieldType, int Level);

    private sealed class RepairState
    {
        private readonly Dictionary<int, PlannedBuilding> _slots = new();
        private readonly Dictionary<string, int> _levelsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _levelsByGid = new();
        private readonly HashSet<int> _blockedSlots = [];
        private readonly List<ActiveConstruction> _activeConstructions;
        private readonly List<PlannedResourceField> _resourceFields = [];

        private RepairState(IReadOnlyList<ActiveConstruction> activeConstructions)
        {
            _activeConstructions = activeConstructions.ToList();
        }

        public static RepairState From(
            VillageStatus status,
            IReadOnlyList<QueueItem> queueItems,
            DateTimeOffset now)
        {
            var state = new RepairState(ConstructionQueueState.ResolveCurrentActiveConstructions(status, now));
            foreach (var building in status.Buildings)
            {
                if (building.SlotId is not int slotId)
                {
                    continue;
                }

                var gid = building.Gid ?? BuildingCatalogService.GidForName(building.Name) ?? 0;
                var level = Math.Max(0, building.Level ?? 0);
                if (level > 0 || gid > 0)
                {
                    state.ApplyBuilding(slotId, gid, building.Name, level);
                }
            }

            foreach (var field in status.ResourceFields)
            {
                if (field.SlotId is not int slotId)
                {
                    continue;
                }

                var level = Math.Max(0, field.Level ?? 0);
                state._resourceFields.Add(new PlannedResourceField(slotId, field.Name, field.FieldType, level));
                state.ApplyResource(field.Name, level);
                state.ApplyResource(field.FieldType, level);
            }

            foreach (var active in state._activeConstructions)
            {
                if (active.SlotId is int slotId)
                {
                    state._blockedSlots.Add(slotId);
                }
            }

            foreach (var item in queueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
            {
                if (BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                    && construct is not null
                    && string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
                {
                    state._blockedSlots.Add(construct.SlotId);
                    continue;
                }

                if (BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
                    && upgrade is not null
                    && (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)))
                {
                    state._blockedSlots.Add(upgrade.SlotId);
                    continue;
                }

                if (string.Equals(item.TaskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && item.Payload.TryGetValue(BotOptionPayloadKeys.TargetBuildingSlotOrName, out var slotRaw)
                    && int.TryParse(slotRaw, out var demolishSlot))
                {
                    state._blockedSlots.Add(demolishSlot);
                }
            }

            return state;
        }

        public PlannedBuilding? FindExistingBuilding(int gid, string name)
        {
            return _slots.Values
                .Where(item => item.Level > 0)
                .Where(item => item.Gid == gid
                    || NameMatches(item.Name, name)
                    || (WallGids.Contains(gid) && WallGids.Contains(item.Gid)))
                .OrderByDescending(item => item.Level)
                .FirstOrDefault();
        }

        public ActiveConstruction? FindActiveBuilding(int gid, string name, DateTimeOffset now)
        {
            return _activeConstructions
                .Where(item => !(item.Finish?.IsFinishedAt(now) ?? false))
                .Where(item => item.Kind != ConstructionKind.Resource)
                .Where(item => item.Gid == gid
                    || NameMatches(item.Name, name)
                    || (WallGids.Contains(gid) && item.Gid is int activeGid && WallGids.Contains(activeGid)))
                .OrderByDescending(item => item.Level ?? 0)
                .FirstOrDefault();
        }

        public PlannedResourceField? FindResourceField(string requirementName)
        {
            var category = ResourceCategory(requirementName);
            return _resourceFields
                .Where(field => string.Equals(ResourceCategory(field.Name), category, StringComparison.Ordinal)
                    || string.Equals(ResourceCategory(field.FieldType), category, StringComparison.Ordinal))
                .OrderByDescending(field => field.Level)
                .ThenBy(field => field.SlotId)
                .FirstOrDefault();
        }

        public int ResourceLevelFor(string requirementName)
        {
            var category = ResourceCategory(requirementName);
            if (category is not null && _levelsByName.TryGetValue(category, out var resourceLevel))
            {
                return resourceLevel;
            }

            return _levelsByName.TryGetValue(NormalizeName(requirementName), out var level) ? level : 0;
        }

        public bool HasGid(int gid)
        {
            return _levelsByGid.GetValueOrDefault(gid) > 0
                || _slots.Values.Any(item => item.Gid == gid && item.Level > 0);
        }

        public bool HasGidPresence(int gid)
        {
            return _slots.Values.Any(item => item.Gid == gid);
        }

        public int HighestLevelForGid(int gid)
        {
            return _slots.Values
                .Where(item => item.Gid == gid)
                .Select(item => item.Level)
                .DefaultIfEmpty(0)
                .Max();
        }

        public bool HasAnyWall()
        {
            return _slots.Values.Any(item => WallGids.Contains(item.Gid) && item.Level > 0);
        }

        public bool IsSlotFreeForNewConstruct(int slotId, int gid)
        {
            if (_blockedSlots.Contains(slotId))
            {
                return false;
            }

            return IsLiveSlotFree(slotId, gid);
        }

        public bool CanReuseQueuedConstructSlot(int slotId, int gid)
        {
            return IsLiveSlotFree(slotId, gid);
        }

        public void ApplyBuilding(int slotId, int gid, string name, int level)
        {
            if (slotId > 0)
            {
                _slots[slotId] = new PlannedBuilding(slotId, gid, name, Math.Max(0, level));
                _blockedSlots.Add(slotId);
            }

            var normalizedName = NormalizeName(name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                _levelsByName[normalizedName] = Math.Max(_levelsByName.GetValueOrDefault(normalizedName), level);
            }

            if (gid > 0)
            {
                _levelsByGid[gid] = Math.Max(_levelsByGid.GetValueOrDefault(gid), level);
            }

            if (WallGids.Contains(gid))
            {
                _levelsByName["wall"] = Math.Max(_levelsByName.GetValueOrDefault("wall"), level);
            }
        }

        public void ApplyResource(string? name, int level)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var normalized = NormalizeName(name);
            _levelsByName[normalized] = Math.Max(_levelsByName.GetValueOrDefault(normalized), level);
            var category = ResourceCategory(name);
            if (category is not null)
            {
                _levelsByName[category] = Math.Max(_levelsByName.GetValueOrDefault(category), level);
            }
        }

        private bool IsLiveSlotFree(int slotId, int gid)
        {
            if (!_slots.TryGetValue(slotId, out var existing))
            {
                return true;
            }

            if (existing.Level > 0)
            {
                return false;
            }

            return existing.Gid == gid && IsUnbuiltFixedSpecialSlot(slotId, gid);
        }

        private static bool IsUnbuiltFixedSpecialSlot(int slotId, int gid)
        {
            return (slotId == RallyPointSlotId && gid == 16)
                || (slotId == WallSlotId && WallGids.Contains(gid));
        }
    }
}
