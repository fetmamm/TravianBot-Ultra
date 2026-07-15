using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed record BuildingTemplatePlanAction(
    string TaskName,
    Dictionary<string, string> Payload,
    string DisplayName,
    int SlotId,
    int? Gid,
    int? TargetLevel,
    double Seconds,
    long Wood,
    long Clay,
    long Iron,
    long Crop);

public sealed record BuildingTemplatePlanResult(
    IReadOnlyList<BuildingTemplatePlanAction> Actions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    double Seconds,
    long Wood,
    long Clay,
    long Iron,
    long Crop)
{
    public bool CanQueue => Errors.Count == 0 && Actions.Count > 0;
}

public enum BuildingTemplateAvailability
{
    Available,
    MissingRequirements,
    Unavailable,
}

public sealed record BuildingTemplateAvailabilityResult(
    BuildingTemplateAvailability Availability,
    string Reason);

public sealed record BuildingTemplatePrerequisitePlan(
    IReadOnlyList<BuildingTemplateRow> Rows,
    IReadOnlyList<string> Blockers);

public sealed class BuildingTemplatePlanner
{
    private static readonly HashSet<int> WallGids = [31, 32, 33, 42, 43];
    private static readonly HashSet<int> UnsupportedPlanGids = [38, 39, 40];
    private static readonly HashSet<int> DuplicateAllowedGids = [10, 11, 23, 38, 39];
    private static readonly HashSet<int> FixedSlotGids = [16, 31, 32, 33, 42, 43];
    private const int RallyPointSlotId = 39;
    private const int WallSlotId = 40;

    public BuildingTemplatePlanResult Plan(
        IReadOnlyList<BuildingTemplateRow> rows,
        VillageStatus status,
        double serverSpeed,
        int mainBuildingLevel)
    {
        var actions = new List<BuildingTemplatePlanAction>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var state = ProjectedVillageState.From(status);

        double totalSeconds = 0;
        long totalWood = 0, totalClay = 0, totalIron = 0, totalCrop = 0;

        var planRows = rows ?? [];
        for (var rowIndex = 0; rowIndex < planRows.Count; rowIndex++)
        {
            var row = planRows[rowIndex];
            if (row.Kind == BuildingTemplateRowKind.AllResources)
            {
                var target = Math.Clamp(row.TargetLevel, 1, 20);
                var resourceActions = PlanResources(row, status, state, target, serverSpeed, mainBuildingLevel, warnings);
                actions.AddRange(resourceActions);
                if (resourceActions.Count > 0)
                {
                    state.ApplyResources(ResourceScope(row), target);
                }
                foreach (var resourceAction in resourceActions)
                {
                    AddTotals(resourceAction);
                }
                continue;
            }

            if (!TryResolveBuilding(row, status.Tribe, warnings, out var gid, out var name, out var skipped))
            {
                errors.Add(skipped
                    ? $"Row {RowLabel(row)} cannot be queued for tribe {status.Tribe}."
                    : $"Row {RowLabel(row)} has no valid building.");
                continue;
            }

            var targetLevel = Math.Clamp(Math.Max(1, row.TargetLevel), 1, BuildingCatalogService.MaxLevelFor(gid));
            var requirements = BuildingCatalogService.RequirementsFor(gid);
            var missing = MissingRequirements(requirements, state);
            if (missing.Count > 0)
            {
                errors.Add($"{name} requires {string.Join(", ", missing.Select(req => $"{req.Name} {req.Level}+"))} before this row.");
                continue;
            }

            var existing = state.FindExistingBuilding(gid, name);
            if (existing is not null)
            {
                var existingValue = existing.Value;
                if (existingValue.Level < targetLevel)
                {
                    var upgrade = PlanBuildingUpgrade(existingValue.SlotId, gid, name, existingValue.Level, targetLevel, serverSpeed, mainBuildingLevel);
                    actions.Add(upgrade);
                    state.ApplyBuilding(existingValue.SlotId, gid, name, targetLevel);
                    AddTotals(upgrade);
                }
                else
                {
                    state.ApplyBuilding(existingValue.SlotId, gid, name, existingValue.Level);
                }
                continue;
            }

            var futureReservedSlots = planRows
                .Skip(rowIndex + 1)
                .Where(item => item.Kind == BuildingTemplateRowKind.Building)
                .Select(item => item.PreferredSlotId)
                .OfType<int>()
                .Where(slot => slot is >= 19 and <= 38)
                .ToHashSet();
            var slotId = ResolveSlot(row.PreferredSlotId, gid, state, futureReservedSlots);
            if (slotId is null)
            {
                warnings.Add($"Skipped {name}: no valid free building slot is available.");
                continue;
            }

            if (!CanConstructNew(gid, name, status, state, out var reason))
            {
                errors.Add(reason);
                continue;
            }

            var construct = PlanConstruct(slotId.Value, gid, name, serverSpeed, mainBuildingLevel);
            var templateStepId = Guid.NewGuid().ToString("N");
            construct.Payload[BotOptionPayloadKeys.BuildingTemplateStepId] = templateStepId;
            actions.Add(construct);
            AddTotals(construct);
            state.ApplyBuilding(slotId.Value, gid, name, Math.Max(1, targetLevel));

            if (targetLevel > 1)
            {
                var upgrade = PlanBuildingUpgrade(slotId.Value, gid, name, 1, targetLevel, serverSpeed, mainBuildingLevel);
                upgrade.Payload[BotOptionPayloadKeys.BuildingTemplateStepId] = templateStepId;
                actions.Add(upgrade);
                AddTotals(upgrade);
            }
        }

        AddTemplateSlotFallbackMetadata(actions);
        return new BuildingTemplatePlanResult(actions, warnings, errors, totalSeconds, totalWood, totalClay, totalIron, totalCrop);

        void AddTotals(BuildingTemplatePlanAction action)
        {
            totalSeconds += action.Seconds;
            totalWood += action.Wood;
            totalClay += action.Clay;
            totalIron += action.Iron;
            totalCrop += action.Crop;
        }
    }

    private static void AddTemplateSlotFallbackMetadata(IReadOnlyList<BuildingTemplatePlanAction> actions)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (!string.Equals(action.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                || action.SlotId is < 19 or > 38)
            {
                continue;
            }

            var futureSlots = actions
                .Skip(index + 1)
                .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SlotId)
                .Where(slot => slot is >= 19 and <= 38)
                .Distinct()
                .OrderBy(slot => slot);
            action.Payload[BotOptionPayloadKeys.BuildingConstructAllowSlotFallback] = bool.TrueString;
            action.Payload[BotOptionPayloadKeys.BuildingConstructFallbackExcludedSlots] = string.Join(",", futureSlots);
        }
    }

    public BuildingTemplateAvailabilityResult EvaluateBuildingAvailability(
        int gid,
        IReadOnlyList<BuildingTemplateRow> precedingRows,
        VillageStatus status,
        double serverSpeed,
        int mainBuildingLevel)
    {
        var entry = BuildingCatalogService.GetFullCatalog(status.Tribe).FirstOrDefault(item => item.Gid == gid);
        if (entry is null || UnsupportedPlanGids.Contains(gid))
        {
            return new(BuildingTemplateAvailability.Unavailable, "This building cannot be used in a template.");
        }

        if (!entry.MatchesPlayerTribe)
        {
            return new(
                BuildingTemplateAvailability.Unavailable,
                $"Only available for {entry.RequiredTribe ?? "another tribe"}.");
        }

        var state = BuildProjectedState(precedingRows, status, serverSpeed, mainBuildingLevel);

        var missing = MissingRequirements(entry.Requirements, state);
        if (missing.Count > 0)
        {
            return new(
                BuildingTemplateAvailability.MissingRequirements,
                $"Requires {string.Join(", ", missing.Select(item => $"{item.Name} {item.Level}+"))} before this row.");
        }

        if (state.FindExistingBuilding(gid, entry.Name) is not null)
        {
            return new(BuildingTemplateAvailability.Available, "Available: the building already exists.");
        }

        if (!CanConstructNew(gid, entry.Name, status, state, out var reason))
        {
            return new(BuildingTemplateAvailability.Unavailable, reason);
        }

        if (ResolveSlot(null, gid, state, reservedSlots: null) is null)
        {
            return new(BuildingTemplateAvailability.Unavailable, "No valid free building slot is available.");
        }

        return new(BuildingTemplateAvailability.Available, "Available to build at this point in the template.");
    }

    public BuildingTemplatePrerequisitePlan PlanMissingPrerequisites(
        int gid,
        IReadOnlyList<BuildingTemplateRow> precedingRows,
        VillageStatus status,
        double serverSpeed,
        int mainBuildingLevel,
        int? reservedSlotId = null)
    {
        var rows = new List<BuildingTemplateRow>();
        var blockers = new List<string>();
        var stack = new HashSet<int>();
        var state = BuildProjectedState(precedingRows, status, serverSpeed, mainBuildingLevel);
        var reservedSlots = reservedSlotId is >= 19 and <= 38
            ? new HashSet<int> { reservedSlotId.Value }
            : [];

        foreach (var requirement in BuildingCatalogService.RequirementsFor(gid))
        {
            EnsureRequirement(requirement);
        }

        return new BuildingTemplatePrerequisitePlan(rows, blockers);

        void EnsureRequirement(BuildingRequirementEntry requirement)
        {
            if (state.LevelForRequirement(requirement.Name) >= requirement.Level)
            {
                return;
            }

            var requirementGid = BuildingCatalogService.GidForName(requirement.Name);
            if (requirementGid is null)
            {
                blockers.Add($"Cannot resolve prerequisite {requirement.Name} {requirement.Level}+.");
                return;
            }

            if (requirementGid is >= 1 and <= 4)
            {
                var scope = ResourceScopeForRequirement(requirement.Name);
                rows.Add(new BuildingTemplateRow
                {
                    Kind = BuildingTemplateRowKind.AllResources,
                    BuildingName = ResourceScopeDisplayName(scope),
                    TargetLevel = requirement.Level,
                    ResourceScope = scope,
                    ResourceStrategy = "lowest",
                });
                state.ApplyResources(scope, requirement.Level);
                return;
            }

            EnsureBuilding(requirementGid.Value, requirement.Level);
        }

        void EnsureBuilding(int requirementGid, int targetLevel)
        {
            var entry = BuildingCatalogService.GetFullCatalog(status.Tribe)
                .FirstOrDefault(item => item.Gid == requirementGid);
            if (entry is null || !entry.MatchesPlayerTribe || UnsupportedPlanGids.Contains(requirementGid))
            {
                blockers.Add($"{BuildingCatalogService.NameForGid(requirementGid)} is not available for {status.Tribe}.");
                return;
            }

            if (!stack.Add(requirementGid))
            {
                blockers.Add($"Circular prerequisite detected for {entry.Name}.");
                return;
            }

            var blockerCountBeforeNestedRequirements = blockers.Count;
            foreach (var nestedRequirement in entry.Requirements)
            {
                EnsureRequirement(nestedRequirement);
            }
            stack.Remove(requirementGid);

            if (blockers.Count > blockerCountBeforeNestedRequirements)
            {
                return;
            }

            if (state.LevelForRequirement(entry.Name) >= targetLevel)
            {
                return;
            }

            var existing = state.FindExistingBuilding(requirementGid, entry.Name);
            int? projectedSlot;
            if (existing is not null)
            {
                projectedSlot = existing.Value.SlotId;
            }
            else
            {
                if (!CanConstructNew(requirementGid, entry.Name, status, state, out var reason))
                {
                    blockers.Add(reason);
                    return;
                }

                projectedSlot = ResolveSlot(null, requirementGid, state, reservedSlots);
                if (projectedSlot is null)
                {
                    blockers.Add($"No free building slot is available for prerequisite {entry.Name}.");
                    return;
                }
            }

            rows.Add(new BuildingTemplateRow
            {
                Kind = BuildingTemplateRowKind.Building,
                Gid = requirementGid,
                BuildingName = entry.Name,
                PreferredSlotId = existing?.SlotId,
                TargetLevel = targetLevel,
                ResourceScope = "all",
                ResourceStrategy = "lowest",
            });
            state.ApplyBuilding(projectedSlot.Value, requirementGid, entry.Name, targetLevel);
        }
    }

    private ProjectedVillageState BuildProjectedState(
        IReadOnlyList<BuildingTemplateRow> rows,
        VillageStatus status,
        double serverSpeed,
        int mainBuildingLevel)
    {
        var state = ProjectedVillageState.From(status);
        foreach (var row in rows.Where(item => item.Kind == BuildingTemplateRowKind.AllResources))
        {
            state.ApplyResources(ResourceScope(row), Math.Max(1, row.TargetLevel));
        }

        var plan = Plan(rows, status, serverSpeed, mainBuildingLevel);
        foreach (var action in plan.Actions.Where(item => item.Gid.HasValue))
        {
            state.ApplyBuilding(
                action.SlotId,
                action.Gid!.Value,
                BuildingCatalogService.NameForGid(action.Gid.Value),
                Math.Max(1, action.TargetLevel ?? 1));
        }

        return state;
    }

    private static IReadOnlyList<BuildingTemplatePlanAction> PlanResources(
        BuildingTemplateRow row,
        VillageStatus status,
        ProjectedVillageState state,
        int targetLevel,
        double serverSpeed,
        int mainBuildingLevel,
        List<string> warnings)
    {
        var scope = ResourceScope(row);
        if (scope != "all")
        {
            return PlanResourceGroup(status, state, scope, targetLevel, serverSpeed, mainBuildingLevel, warnings);
        }

        var action = PlanAllResources(row, status, state, targetLevel, serverSpeed, mainBuildingLevel, warnings);
        return action is null ? [] : [action];
    }

    private static BuildingTemplatePlanAction? PlanAllResources(
        BuildingTemplateRow row,
        VillageStatus status,
        ProjectedVillageState state,
        int targetLevel,
        double serverSpeed,
        int mainBuildingLevel,
        List<string> warnings)
    {
        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        var needsUpgrade = false;
        foreach (var field in status.ResourceFields)
        {
            var currentLevel = state.ResourceLevel(field.Name, field.FieldType, field.Level ?? 0);
            if (currentLevel < targetLevel)
            {
                needsUpgrade = true;
            }
            var gid = BuildingCatalogService.GidForName(field.Name)
                ?? BuildingCatalogService.GidForName(field.FieldType);
            if (gid is null)
            {
                continue;
            }

            for (var level = currentLevel + 1; level <= targetLevel; level++)
            {
                var stats = BuildingCatalogService.CostFor(gid.Value, level);
                if (stats is null)
                {
                    warnings.Add($"Missing resource estimate for {field.Name} level {level}.");
                    continue;
                }

                seconds += BuildingCatalogService.BuildSecondsFor(gid.Value, level, serverSpeed, mainBuildingLevel);
                wood += stats.Wood;
                clay += stats.Clay;
                iron += stats.Iron;
                crop += stats.Crop;
            }
        }

        if (!needsUpgrade)
        {
            warnings.Add($"All resources already meet level {targetLevel} or no resource fields were loaded.");
            return null;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = targetLevel.ToString(),
            [BotOptionPayloadKeys.ResourceBuildStrategy] = string.IsNullOrWhiteSpace(row.ResourceStrategy)
                ? "lowest"
                : row.ResourceStrategy,
        };

        return new BuildingTemplatePlanAction(
            "upgrade_all_resources_to_level",
            payload,
            $"Upgrade all resources to level {targetLevel}",
            0,
            null,
            targetLevel,
            seconds,
            wood,
            clay,
            iron,
            crop);
    }

    private static IReadOnlyList<BuildingTemplatePlanAction> PlanResourceGroup(
        VillageStatus status,
        ProjectedVillageState state,
        string scope,
        int targetLevel,
        double serverSpeed,
        int mainBuildingLevel,
        List<string> warnings)
    {
        var actions = new List<BuildingTemplatePlanAction>();
        foreach (var field in status.ResourceFields
                     .Where(field => field.SlotId is >= 1 and <= 18)
                     .Where(field => string.Equals(ResourceScope(field.Name, field.FieldType), scope, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(field => field.SlotId))
        {
            var slotId = field.SlotId!.Value;
            var currentLevel = state.ResourceLevel(field.Name, field.FieldType, field.Level ?? 0);
            if (currentLevel >= targetLevel)
            {
                continue;
            }

            var gid = BuildingCatalogService.GidForName(field.Name)
                ?? BuildingCatalogService.GidForName(field.FieldType);
            if (gid is null)
            {
                warnings.Add($"Missing resource estimate for slot {slotId}.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(field.Name) ? field.FieldType : field.Name;
            double seconds = 0;
            long wood = 0, clay = 0, iron = 0, crop = 0;
            for (var level = currentLevel + 1; level <= targetLevel; level++)
            {
                var stats = BuildingCatalogService.CostFor(gid.Value, level);
                if (stats is null)
                {
                    warnings.Add($"Missing resource estimate for {name} level {level}.");
                    continue;
                }

                seconds += BuildingCatalogService.BuildSecondsFor(gid.Value, level, serverSpeed, mainBuildingLevel);
                wood += stats.Wood;
                clay += stats.Clay;
                iron += stats.Iron;
                crop += stats.Crop;
            }

            var payload = new ResourceUpgradePayload(slotId, targetLevel, name).ToDictionary();
            actions.Add(new BuildingTemplatePlanAction(
                "upgrade_resource_to_level",
                payload,
                $"Upgrade {name} to level {targetLevel} (slot {slotId})",
                slotId,
                gid,
                targetLevel,
                seconds,
                wood,
                clay,
                iron,
                crop));
        }

        if (actions.Count == 0)
        {
            warnings.Add($"{ResourceScopeDisplayName(scope)} already meets level {targetLevel} or no matching fields were found.");
        }

        return actions;
    }

    private static BuildingTemplatePlanAction PlanConstruct(
        int slotId,
        int gid,
        string name,
        double serverSpeed,
        int mainBuildingLevel)
    {
        var stats = BuildingCatalogService.CostFor(gid, 1);
        var payload = new BuildingConstructPayload(slotId, gid, name).ToDictionary();
        return new BuildingTemplatePlanAction(
            "construct_building",
            payload,
            $"Construct {name} to level 1 (slot {slotId})",
            slotId,
            gid,
            1,
            BuildingCatalogService.BuildSecondsFor(gid, 1, serverSpeed, mainBuildingLevel),
            stats?.Wood ?? 0,
            stats?.Clay ?? 0,
            stats?.Iron ?? 0,
            stats?.Crop ?? 0);
    }

    private static BuildingTemplatePlanAction PlanBuildingUpgrade(
        int slotId,
        int gid,
        string name,
        int currentLevel,
        int targetLevel,
        double serverSpeed,
        int mainBuildingLevel)
    {
        double seconds = 0;
        long wood = 0, clay = 0, iron = 0, crop = 0;
        for (var level = currentLevel + 1; level <= targetLevel; level++)
        {
            var stats = BuildingCatalogService.CostFor(gid, level);
            if (stats is null)
            {
                continue;
            }

            seconds += BuildingCatalogService.BuildSecondsFor(gid, level, serverSpeed, mainBuildingLevel);
            wood += stats.Wood;
            clay += stats.Clay;
            iron += stats.Iron;
            crop += stats.Crop;
        }

        var payload = new BuildingUpgradePayload(slotId, targetLevel, name).ToDictionary();
        return new BuildingTemplatePlanAction(
            "upgrade_building_to_level",
            payload,
            $"Upgrade {name} to level {targetLevel} (slot {slotId})",
            slotId,
            gid,
            targetLevel,
            seconds,
            wood,
            clay,
            iron,
            crop);
    }

    private static bool TryResolveBuilding(
        BuildingTemplateRow row,
        string tribe,
        List<string> warnings,
        out int gid,
        out string name,
        out bool skipped)
    {
        gid = row.Gid ?? BuildingCatalogService.GidForName(row.BuildingName) ?? 0;
        name = row.BuildingName;
        skipped = false;

        if (WallGids.Contains(gid))
        {
            var wall = BuildingCatalogService.WallForTribe(tribe);
            if (wall is null)
            {
                warnings.Add($"Skipped wall row: no supported wall for tribe {tribe}.");
                skipped = true;
                return false;
            }

            gid = wall.Value.Gid;
            name = wall.Value.Name;
            return true;
        }

        var resolvedGid = gid;
        var entry = BuildingCatalogService.GetFullCatalog(tribe).FirstOrDefault(item => item.Gid == resolvedGid);
        if (entry is null)
        {
            return false;
        }

        if (entry.IsSpecial && !entry.MatchesPlayerTribe)
        {
            warnings.Add($"Skipped {entry.Name}: only available for {entry.RequiredTribe ?? "another tribe"}.");
            skipped = true;
            return false;
        }

        if (UnsupportedPlanGids.Contains(gid))
        {
            warnings.Add($"Skipped {entry.Name}: this building requires building plans and is not template-queueable.");
            skipped = true;
            return false;
        }

        name = entry.Name;
        return true;
    }

    private static int? ResolveSlot(
        int? preferredSlotId,
        int gid,
        ProjectedVillageState state,
        IReadOnlySet<int>? reservedSlots)
    {
        if (gid == 16)
        {
            return state.IsSlotFree(RallyPointSlotId, gid) ? RallyPointSlotId : null;
        }

        if (WallGids.Contains(gid))
        {
            return state.IsSlotFree(WallSlotId, gid) ? WallSlotId : null;
        }

        var validSlots = Enumerable.Range(19, 20).ToList(); // 19-38, excluding fixed special slots.
        if (preferredSlotId is int preferred && validSlots.Contains(preferred))
        {
            var ordered = validSlots.Where(slot => slot >= preferred)
                .Concat(validSlots.Where(slot => slot < preferred));
            return ordered.FirstOrDefault(slot => state.IsSlotFree(slot, gid)
                    && (slot == preferred || reservedSlots?.Contains(slot) != true)) is int match && match > 0
                ? match
                : null;
        }

        return validSlots.FirstOrDefault(slot => state.IsSlotFree(slot, gid)
                && reservedSlots?.Contains(slot) != true) is int autoMatch && autoMatch > 0
            ? autoMatch
            : null;
    }

    private static bool CanConstructNew(int gid, string name, VillageStatus status, ProjectedVillageState state, out string reason)
    {
        reason = string.Empty;
        if ((gid is 29 or 30) && status.IsCapital == true)
        {
            reason = $"{name} cannot be built in the capital.";
            return false;
        }

        var conflictingResidenceFamilyGid = BuildingCatalogService.ResidenceFamilyConflictGidsFor(gid)
            .FirstOrDefault(state.HasGidPresence);
        if (conflictingResidenceFamilyGid > 0)
        {
            reason = $"{name} conflicts with {BuildingCatalogService.NameForGid(conflictingResidenceFamilyGid)}.";
            return false;
        }

        if (BuildingCatalogService.DuplicateRequiredExistingLevelFor(gid) is int duplicateRequiredLevel
            && state.HasGidPresence(gid)
            && state.HighestLevelForGid(gid) < duplicateRequiredLevel)
        {
            reason = $"{name} can only be duplicated after an existing one reaches level {duplicateRequiredLevel}.";
            return false;
        }

        if (BuildingCatalogService.IsSingleInstance(gid) && !DuplicateAllowedGids.Contains(gid) && !FixedSlotGids.Contains(gid))
        {
            reason = $"{name} already exists in this village.";
            return !state.HasGid(gid);
        }

        return true;
    }

    private static List<BuildingRequirementEntry> MissingRequirements(
        IReadOnlyList<BuildingRequirementEntry> requirements,
        ProjectedVillageState state)
    {
        return requirements
            .Where(requirement => state.LevelForRequirement(requirement.Name) < requirement.Level)
            .ToList();
    }

    private static string RowLabel(BuildingTemplateRow row)
        => string.IsNullOrWhiteSpace(row.BuildingName)
            ? row.Gid?.ToString() ?? "building"
            : row.BuildingName;

    private sealed class ProjectedVillageState
    {
        private readonly Dictionary<int, (int Gid, string Name, int Level)> _slots = new();
        private readonly Dictionary<string, int> _levelsByName = new(StringComparer.OrdinalIgnoreCase);

        public static ProjectedVillageState From(VillageStatus status)
        {
            var state = new ProjectedVillageState();
            foreach (var building in status.Buildings)
            {
                if (building.SlotId is not int slotId || building.Gid is not int gid)
                {
                    continue;
                }

                var level = Math.Max(0, building.Level ?? 0);
                if (level > 0 || gid > 0)
                {
                    state.ApplyBuilding(slotId, gid, building.Name, level);
                }
            }

            foreach (var field in status.ResourceFields)
            {
                var level = Math.Max(0, field.Level ?? 0);
                state.ApplyRequirementLevel(field.Name, level);
                state.ApplyRequirementLevel(field.FieldType, level);
            }

            return state;
        }

        public void ApplyAllResources(int targetLevel)
        {
            ApplyRequirementLevel("Woodcutter", targetLevel);
            ApplyRequirementLevel("Clay Pit", targetLevel);
            ApplyRequirementLevel("Iron Mine", targetLevel);
            ApplyRequirementLevel("Cropland", targetLevel);
        }

        public void ApplyResources(string scope, int targetLevel)
        {
            if (scope == "all")
            {
                ApplyAllResources(targetLevel);
                return;
            }

            ApplyRequirementLevel(ResourceScopeDisplayName(scope), targetLevel);
        }

        public void ApplyBuilding(int slotId, int gid, string name, int level)
        {
            _slots[slotId] = (gid, name, Math.Max(0, level));
            ApplyRequirementLevel(name, level);
            if (WallGids.Contains(gid))
            {
                ApplyRequirementLevel("Wall", level);
            }
        }

        public bool IsSlotFree(int slotId, int gid)
        {
            if (!_slots.TryGetValue(slotId, out var existing))
            {
                return true;
            }

            return existing.Level <= 0 && existing.Gid == gid;
        }

        public bool HasGid(int gid)
            => _slots.Values.Any(item => item.Gid == gid && item.Level > 0);

        public bool HasGidPresence(int gid)
            => _slots.Values.Any(item => item.Gid == gid);

        public int HighestLevelForGid(int gid)
            => _slots.Values
                .Where(item => item.Gid == gid)
                .Select(item => item.Level)
                .DefaultIfEmpty(0)
                .Max();

        public (int SlotId, int Level)? FindExistingBuilding(int gid, string name)
        {
            var match = _slots
                .Where(item => item.Value.Level > 0)
                .Where(item => item.Value.Gid == gid
                    || string.Equals(Normalize(item.Value.Name), Normalize(name), StringComparison.OrdinalIgnoreCase)
                    || (WallGids.Contains(gid) && WallGids.Contains(item.Value.Gid)))
                .OrderByDescending(item => item.Value.Level)
                .FirstOrDefault();
            return match.Key == 0 ? null : (match.Key, match.Value.Level);
        }

        public int LevelForRequirement(string name)
        {
            if (_levelsByName.TryGetValue(Normalize(name), out var level))
            {
                return level;
            }

            var resourceCategory = ResourceCategory(name);
            if (resourceCategory is not null && _levelsByName.TryGetValue(resourceCategory, out var resourceLevel))
            {
                return resourceLevel;
            }

            return 0;
        }

        public int ResourceLevel(string? name, string? fieldType, int fallback)
        {
            var fromName = !string.IsNullOrWhiteSpace(name) ? LevelForRequirement(name) : 0;
            var fromType = !string.IsNullOrWhiteSpace(fieldType) ? LevelForRequirement(fieldType) : 0;
            return Math.Max(fallback, Math.Max(fromName, fromType));
        }

        private void ApplyRequirementLevel(string? name, int level)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var key = Normalize(name);
            _levelsByName[key] = Math.Max(_levelsByName.GetValueOrDefault(key), level);
            var resourceCategory = ResourceCategory(name);
            if (resourceCategory is not null)
            {
                _levelsByName[resourceCategory] = Math.Max(_levelsByName.GetValueOrDefault(resourceCategory), level);
            }
        }

        private static string Normalize(string? value)
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
    }

    private static string ResourceScope(BuildingTemplateRow row)
        => NormalizeResourceScope(row.ResourceScope);

    private static string ResourceScope(string? name, string? fieldType)
        => NormalizeResourceScope(!string.IsNullOrWhiteSpace(fieldType) ? fieldType : name);

    private static string NormalizeResourceScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "all";
        }

        if (value.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (value.Contains("Clay", StringComparison.OrdinalIgnoreCase)) return "clay";
        if (value.Contains("Iron", StringComparison.OrdinalIgnoreCase)) return "iron";
        if (value.Contains("Crop", StringComparison.OrdinalIgnoreCase)) return "crop";
        return string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) ? "all" : value.Trim().ToLowerInvariant();
    }

    private static string ResourceScopeDisplayName(string scope)
        => scope switch
        {
            "wood" => "Woodcutter",
            "clay" => "Clay Pit",
            "iron" => "Iron Mine",
            "crop" => "Cropland",
            _ => "All resources",
        };

    private static string ResourceScopeForRequirement(string name)
    {
        if (name.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (name.Contains("Clay", StringComparison.OrdinalIgnoreCase)) return "clay";
        if (name.Contains("Iron", StringComparison.OrdinalIgnoreCase)) return "iron";
        if (name.Contains("Crop", StringComparison.OrdinalIgnoreCase)) return "crop";
        return "all";
    }
}
