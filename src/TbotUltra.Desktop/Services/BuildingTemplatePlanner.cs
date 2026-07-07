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

        foreach (var row in rows ?? [])
        {
            if (row.Kind == BuildingTemplateRowKind.AllResources)
            {
                var target = Math.Max(1, row.TargetLevel);
                var resourceActions = PlanResources(row, status, state, target, serverSpeed, mainBuildingLevel, warnings);
                actions.AddRange(resourceActions);
                state.ApplyResources(ResourceScope(row), target);
                foreach (var resourceAction in resourceActions)
                {
                    AddTotals(resourceAction);
                }
                continue;
            }

            if (!TryResolveBuilding(row, status.Tribe, warnings, out var gid, out var name, out var skipped))
            {
                if (!skipped)
                {
                    errors.Add($"Row {RowLabel(row)} has no valid building.");
                }
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

            var slotId = ResolveSlot(row.PreferredSlotId, gid, state);
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
            actions.Add(construct);
            AddTotals(construct);
            state.ApplyBuilding(slotId.Value, gid, name, Math.Max(1, targetLevel));

            if (targetLevel > 1)
            {
                var upgrade = PlanBuildingUpgrade(slotId.Value, gid, name, 1, targetLevel, serverSpeed, mainBuildingLevel);
                actions.Add(upgrade);
                AddTotals(upgrade);
            }
        }

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
        return scope == "all"
            ? [PlanAllResources(row, status, state, targetLevel, serverSpeed, mainBuildingLevel, warnings)]
            : PlanResourceGroup(status, state, scope, targetLevel, serverSpeed, mainBuildingLevel, warnings);
    }

    private static BuildingTemplatePlanAction PlanAllResources(
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
        foreach (var field in status.ResourceFields)
        {
            var currentLevel = state.ResourceLevel(field.Name, field.FieldType, field.Level ?? 0);
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

    private static int? ResolveSlot(int? preferredSlotId, int gid, ProjectedVillageState state)
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
            return ordered.FirstOrDefault(slot => state.IsSlotFree(slot, gid)) is int match && match > 0
                ? match
                : null;
        }

        return validSlots.FirstOrDefault(slot => state.IsSlotFree(slot, gid)) is int autoMatch && autoMatch > 0
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

        if (gid == 26 && (state.HasGid(25) || state.HasGid(44)))
        {
            reason = "Palace conflicts with Residence or Command Center.";
            return false;
        }

        if (gid == 25 && (state.HasGid(26) || state.HasGid(44)))
        {
            reason = "Residence conflicts with Palace or Command Center.";
            return false;
        }

        if (gid == 44 && (state.HasGid(25) || state.HasGid(26)))
        {
            reason = "Command Center conflicts with Palace or Residence.";
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
}
