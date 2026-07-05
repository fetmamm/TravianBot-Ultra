using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed record ConstructionDependencyDelay(TimeSpan Delay, string Detail);

public static class ConstructionDependencyGate
{
    private static readonly TimeSpan PrerequisiteFinishBuffer = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnknownActivePrerequisiteRetry = TimeSpan.FromSeconds(60);

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
                .Where(requirement => ActiveConstructionSatisfiesRequirement(active, requirement))
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

    private static bool ActiveConstructionSatisfiesRequirement(
        ActiveConstruction active,
        BuildingRequirementEntry requirement)
    {
        return active.Level is int level
            && level >= requirement.Level
            && !string.IsNullOrWhiteSpace(active.Name)
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
}
