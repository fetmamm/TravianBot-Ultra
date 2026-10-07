using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed record ConstructionDependencyDelay(TimeSpan Delay, string Detail);

public enum ConstructionRequirementGuardAction
{
    None,
    DeferForActivePrerequisite,
    DeferForQueuedPrerequisite,
    FailMissingPrerequisite,
}

public sealed record ConstructionRequirementGuardResult(
    ConstructionRequirementGuardAction Action,
    TimeSpan? Delay,
    string Detail)
{
    public static ConstructionRequirementGuardResult None { get; } =
        new(ConstructionRequirementGuardAction.None, null, string.Empty);
}

public static class ConstructionDependencyGate
{
    private static readonly TimeSpan PrerequisiteFinishBuffer = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnknownActivePrerequisiteRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueuedPrerequisiteRetry = TimeSpan.FromSeconds(60);

    public static bool CanEvaluateConstructRequirementsFromSnapshot(
        QueueItem item,
        VillageStatus status)
    {
        if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
            || payload is null
            || !status.ActiveConstructionsFromOverview)
        {
            return false;
        }

        var requirements = BuildingCatalogService.RequirementsFor(payload.Gid);
        var needsResourceFields = requirements.Any(requirement =>
            BuildingCatalogService.GidForName(requirement.Name) is >= 1 and <= 4);
        if (needsResourceFields
            && status.ResourceFields
                .Where(field => field.SlotId is >= 1 and <= 18 && field.Level is not null)
                .Select(field => field.SlotId!.Value)
                .Distinct()
                .Count() != 18)
        {
            return false;
        }

        var needsBuildings = requirements.Any(requirement =>
            BuildingCatalogService.GidForName(requirement.Name) is not (>= 1 and <= 4));
        return !needsBuildings
            || status.Buildings
                .Where(building => building.SlotId is >= 19 and <= 43 && building.Level is not null)
                .Select(building => building.SlotId!.Value)
                .Distinct()
                .Count() == 22;
    }

    public static ConstructionDependencyDelay? ResolveUpgradeWaitingForConstruct(
        QueueItem item,
        IReadOnlyList<QueueItem> sameVillageQueueItems,
        DateTimeOffset now)
    {
        if ((!string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            || !BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
            || upgrade is null)
        {
            return null;
        }

        var expectedGid = BuildingCatalogService.GidForName(upgrade.Name);
        var dependency = sameVillageQueueItems
            .Where(candidate => ConstructionQueueState.IsActiveQueueStatus(candidate.Status))
            .Where(candidate => string.Equals(candidate.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new
            {
                Item = candidate,
                Parsed = BuildingConstructPayload.TryFromDictionary(candidate.Payload, out var construct)
                    ? construct
                    : null,
            })
            .FirstOrDefault(candidate => candidate.Parsed is not null
                && candidate.Parsed.SlotId == upgrade.SlotId
                && (expectedGid is int gid
                    ? candidate.Parsed.Gid == gid
                    : string.Equals(
                        candidate.Parsed.Name?.Trim(),
                        upgrade.Name?.Trim(),
                        StringComparison.OrdinalIgnoreCase)));
        if (dependency?.Parsed is null)
        {
            return null;
        }

        var delay = dependency.Item.NextAttemptAt is DateTimeOffset retryAt && retryAt > now
            ? retryAt - now
            : QueuedPrerequisiteRetry;
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1);
        }

        var buildingName = string.IsNullOrWhiteSpace(upgrade.Name)
            ? $"gid {dependency.Parsed.Gid}"
            : upgrade.Name;
        return new ConstructionDependencyDelay(
            delay,
            $"{buildingName} construct in slot {upgrade.SlotId}");
    }

    public static ConstructionDependencyDelay? ResolveConstructDelay(
        QueueItem item,
        VillageStatus status,
        DateTimeOffset now)
    {
        if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
            || payload is null)
        {
            return null;
        }

        var requirements = BuildingCatalogService.RequirementsFor(payload.Gid);
        if (requirements.Count == 0)
        {
            return null;
        }

        var missing = ResolveMissingFinishedRequirements(status, requirements);
        if (missing.Count == 0)
        {
            return null;
        }

        var activeMatches = ConstructionQueueState.ResolveCurrentActiveConstructions(status, now)
            .SelectMany(active => missing
                .Where(requirement => ActiveConstructionMatchesRequirement(active, requirement))
                .Select(requirement => (Active: active, Requirement: requirement)))
            .ToList();
        if (activeMatches.Count == 0)
        {
            return null;
        }

        var waitSeconds = activeMatches
            .Select(match => ResolveRemainingSeconds(match.Active, now))
            .DefaultIfEmpty((int)Math.Ceiling(UnknownActivePrerequisiteRetry.TotalSeconds))
            .Max();
        waitSeconds = Math.Max(1, waitSeconds + (int)Math.Ceiling(PrerequisiteFinishBuffer.TotalSeconds));

        var detail = string.Join(", ", activeMatches
            .Select(match => $"{match.Requirement.Name} {match.Requirement.Level}+")
            .Distinct(StringComparer.OrdinalIgnoreCase));
        return new ConstructionDependencyDelay(TimeSpan.FromSeconds(waitSeconds), detail);
    }

    public static ConstructionRequirementGuardResult ResolveConstructRequirementGuard(
        QueueItem item,
        VillageStatus status,
        IReadOnlyList<QueueItem> sameVillageQueueItems,
        DateTimeOffset now)
    {
        if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            || !BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
            || payload is null)
        {
            return ConstructionRequirementGuardResult.None;
        }

        var requirements = BuildingCatalogService.RequirementsFor(payload.Gid);
        if (requirements.Count == 0)
        {
            return ConstructionRequirementGuardResult.None;
        }

        var missing = ResolveMissingFinishedRequirements(status, requirements);
        if (missing.Count == 0)
        {
            return ConstructionRequirementGuardResult.None;
        }

        var projectedStatus = ProjectQueuedBuildingStatus(status, sameVillageQueueItems);
        var activeDelay = ResolveConstructDelay(item, status, now);
        if (activeDelay is not null)
        {
            return new ConstructionRequirementGuardResult(
                ConstructionRequirementGuardAction.DeferForActivePrerequisite,
                activeDelay.Delay,
                activeDelay.Detail);
        }

        var unresolved = missing
            .Where(requirement => Math.Max(
                ResolveQueuedRequirementLevel(projectedStatus, sameVillageQueueItems, requirement),
                ResolveActiveRequirementLevel(status, requirement, now)) < requirement.Level)
            .ToList();
        if (unresolved.Count > 0)
        {
            return new ConstructionRequirementGuardResult(
                ConstructionRequirementGuardAction.FailMissingPrerequisite,
                null,
                FormatRequirements(unresolved));
        }

        return new ConstructionRequirementGuardResult(
            ConstructionRequirementGuardAction.DeferForQueuedPrerequisite,
            ResolveQueuedPrerequisiteRetry(missing, sameVillageQueueItems, now),
            FormatRequirements(missing));
    }

    private static TimeSpan ResolveQueuedPrerequisiteRetry(
        IReadOnlyList<BuildingRequirementEntry> missing,
        IReadOnlyList<QueueItem> queueItems,
        DateTimeOffset now)
    {
        var matching = queueItems
            .Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status))
            .Where(item => missing.Any(requirement => QueuedItemProvidesRequirement(item, requirement)))
            .ToList();
        var specific = matching
            .Where(item => !string.Equals(
                item.TaskName,
                "upgrade_all_resources_to_level",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidates = specific.Count > 0 ? specific : matching;
        var retryAt = candidates
            .Where(item => item.NextAttemptAt > now)
            .Select(item => item.NextAttemptAt)
            .DefaultIfEmpty()
            .Max();
        return retryAt > now ? retryAt - now : QueuedPrerequisiteRetry;
    }

    private static bool QueuedItemProvidesRequirement(
        QueueItem item,
        BuildingRequirementEntry requirement)
    {
        if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            && ResourceUpgradePayload.TryFromDictionary(item.Payload, out var resource)
            && resource is not null)
        {
            return string.Equals(
                    ResourceCategory(resource.Name),
                    ResourceCategory(requirement.Name),
                    StringComparison.Ordinal)
                && resource.TargetLevel >= requirement.Level;
        }

        if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
        {
            var category = ResourceCategory(requirement.Name);
            var target = TryGetIntPayloadValue(item.Payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                ?? TryGetIntPayloadValue(item.Payload, BotOptionPayloadKeys.TargetLevel);
            return category is not null
                && target >= requirement.Level
                && ResourceUpgradeSelection.Parse(
                    item.Payload.GetValueOrDefault(BotOptionPayloadKeys.ResourceUpgradeTypes))
                    .Contains(category);
        }

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
            && construct is not null)
        {
            return construct.TargetLevel >= requirement.Level
                && !string.IsNullOrWhiteSpace(construct.Name)
                && construct.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase);
        }

        if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            && BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
            && upgrade is not null
            && !string.IsNullOrWhiteSpace(upgrade.Name)
            && upgrade.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
        {
            var gid = BuildingCatalogService.GidForName(upgrade.Name);
            var target = string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
                && gid is int resolvedGid
                    ? BuildingCatalogService.MaxLevelFor(resolvedGid)
                    : upgrade.TargetLevel ?? 0;
            return target >= requirement.Level;
        }

        return false;
    }

    internal static IReadOnlyList<BuildingRequirementEntry> ResolveMissingFinishedRequirements(
        VillageStatus status,
        IReadOnlyList<BuildingRequirementEntry> requirements)
    {
        var missing = new List<BuildingRequirementEntry>();
        foreach (var requirement in requirements)
        {
            var fromBuildings = status.Buildings
                .Where(item => item.Level is not null
                    && item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var fromResourceFields = status.ResourceFields
                .Where(item => item.Level is not null
                    && (item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase)
                        || (item.FieldType?.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (Math.Max(fromBuildings, fromResourceFields) < requirement.Level)
            {
                missing.Add(requirement);
            }
        }

        return missing;
    }

    private static bool ActiveConstructionMatchesRequirement(
        ActiveConstruction active,
        BuildingRequirementEntry requirement)
    {
        return !string.IsNullOrWhiteSpace(active.Name)
            && active.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveRemainingSeconds(ActiveConstruction active, DateTimeOffset now)
    {
        if (active.Finish is not null)
        {
            return active.Finish.RemainingSecondsAt(now);
        }

        return active.TimeLeftSeconds is > 0
            ? active.TimeLeftSeconds.Value
            : (int)Math.Ceiling(UnknownActivePrerequisiteRetry.TotalSeconds);
    }

    private static VillageStatus ProjectQueuedBuildingStatus(
        VillageStatus status,
        IReadOnlyList<QueueItem> queueItems)
    {
        var projectedBuildings = status.Buildings
            .Select(item => item with { })
            .ToList();
        var bySlot = projectedBuildings
            .Where(item => item.SlotId is not null)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Level ?? 0).First());

        foreach (var item in queueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                && construct is not null)
            {
                var name = string.IsNullOrWhiteSpace(construct.Name) ? $"gid {construct.Gid}" : construct.Name;
                bySlot[construct.SlotId] = new Building(
                    construct.SlotId,
                    name,
                    construct.TargetLevel,
                    null,
                    construct.Gid);
                continue;
            }

            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
                && upgrade is not null
                && bySlot.TryGetValue(upgrade.SlotId, out var currentProjected))
            {
                var currentLevel = currentProjected.Level ?? 0;
                var targetLevel = currentLevel;
                if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && upgrade.TargetLevel.HasValue)
                {
                    targetLevel = Math.Max(currentLevel, upgrade.TargetLevel.Value);
                }
                else if (currentProjected.Gid is int currentGid)
                {
                    targetLevel = Math.Max(currentLevel, BuildingCatalogService.MaxLevelFor(currentGid));
                }

                bySlot[upgrade.SlotId] = currentProjected with { Level = targetLevel };
            }
        }

        return status with { Buildings = bySlot.Values.ToList() };
    }

    private static int ResolveQueuedRequirementLevel(
        VillageStatus projectedStatus,
        IReadOnlyList<QueueItem> queueItems,
        BuildingRequirementEntry requirement)
    {
        var fromBuildings = projectedStatus.Buildings
            .Where(item => item.Level is not null
                && item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Level!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var fromResourceUpgrades = MaxQueuedResourceUpgradeLevel(requirement.Name, queueItems);
        var fromConstructs = QueuedConstructProvidesRequirement(requirement.Name, queueItems) ? 1 : 0;
        return Math.Max(Math.Max(fromBuildings, fromResourceUpgrades), fromConstructs);
    }

    private static int ResolveActiveRequirementLevel(
        VillageStatus status,
        BuildingRequirementEntry requirement,
        DateTimeOffset now)
    {
        return ConstructionQueueState.ResolveCurrentActiveConstructions(status, now)
            .Where(active => active.Level is int level
                && level >= requirement.Level
                && !string.IsNullOrWhiteSpace(active.Name)
                && active.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
            .Select(active => active.Level!.Value)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool QueuedConstructProvidesRequirement(string requirementName, IReadOnlyList<QueueItem> queueItems)
    {
        if (string.IsNullOrWhiteSpace(requirementName))
        {
            return false;
        }

        return queueItems
            .Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status))
            .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .Any(item => BuildingConstructPayload.TryFromDictionary(item.Payload, out var payload)
                && payload is not null
                && !string.IsNullOrWhiteSpace(payload.Name)
                && payload.Name.Contains(requirementName, StringComparison.OrdinalIgnoreCase));
    }

    private static int MaxQueuedResourceUpgradeLevel(string requirementName, IReadOnlyList<QueueItem> queueItems)
    {
        var reqCategory = ResourceCategory(requirementName);
        if (reqCategory is null)
        {
            return 0;
        }

        var best = 0;
        foreach (var item in queueItems.Where(item => ConstructionQueueState.IsActiveQueueStatus(item.Status)))
        {
            var payload = item.Payload;
            int? target = null;
            if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            {
                payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeName, out var name);
                if (string.Equals(ResourceCategory(name), reqCategory, StringComparison.Ordinal))
                {
                    target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                        ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
                }
            }
            else if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
            {
                if (ResourceUpgradeSelection.Parse(payload.GetValueOrDefault(BotOptionPayloadKeys.ResourceUpgradeTypes))
                    .Contains(reqCategory))
                {
                    target = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
                        ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
                }
            }

            if (target is int value)
            {
                best = Math.Max(best, value);
            }
        }

        return best;
    }

    private static int? TryGetIntPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var text) && int.TryParse(text, out var value)
            ? value
            : null;
    }

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

    private static string FormatRequirements(IReadOnlyList<BuildingRequirementEntry> requirements)
    {
        return string.Join(", ", requirements.Select(requirement => $"{requirement.Name} {requirement.Level}+"));
    }
}
