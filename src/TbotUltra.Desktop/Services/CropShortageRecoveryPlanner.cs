using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed record CropShortageRecoveryStep(
    int SlotId,
    int TargetLevel,
    string Name,
    Guid? ExistingQueueItemId);

public sealed record CropShortageRecoveryPlan(
    IReadOnlyList<CropShortageRecoveryStep> Steps,
    bool AllCroplandsAtMax,
    int ActiveCroplandCount,
    double? CropProductionPerHour);

public static class CropShortageRecoveryPlanner
{
    public const int DesiredConcurrentSteps = 2;

    public static bool ShouldResumeParent(
        double cropProductionPerHour,
        int? requiredFreeCrop,
        int completedSteps) =>
        requiredFreeCrop is int required
            ? cropProductionPerHour >= required
            : completedSteps >= DesiredConcurrentSteps;

    public static CropShortageRecoveryPlan Plan(
        VillageStatus status,
        IReadOnlyList<QueueItem> sameVillageQueueItems,
        int maxLevel)
    {
        var croplands = status.ResourceFields
            .Where(field => field.SlotId is >= 1 and <= 18
                && field.Level is >= 0
                && ResourceUpgradeSelection.Matches(field.FieldType, field.Name, new HashSet<string>(["crop"], StringComparer.OrdinalIgnoreCase)))
            .OrderBy(field => field.Level)
            .ThenBy(field => field.SlotId)
            .ToList();

        var activeCroplands = ConstructionQueueState.ResolveCurrentActiveConstructions(status)
            .Where(active => active.Kind == ConstructionKind.Resource
                && (active.Name.Contains("crop", StringComparison.OrdinalIgnoreCase)
                    || active.SlotId is int slotId && croplands.Any(field => field.SlotId == slotId)))
            .ToList();
        var activeCropSlots = activeCroplands
            .Select(active => active.SlotId)
            .Where(slotId => slotId.HasValue)
            .Select(slotId => slotId!.Value)
            .ToHashSet();

        var slotsToPlan = Math.Max(0, DesiredConcurrentSteps - activeCroplands.Count);
        var steps = new List<CropShortageRecoveryStep>(slotsToPlan);
        foreach (var field in croplands
                     .Where(field => field.Level < maxLevel && !activeCropSlots.Contains(field.SlotId!.Value))
                     .Take(slotsToPlan))
        {
            var targetLevel = field.Level!.Value + 1;
            var existing = sameVillageQueueItems.FirstOrDefault(item =>
                item.Status == QueueStatus.Pending
                && string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                && ResourceUpgradePayload.TryFromDictionary(item.Payload, out var payload, maxLevel)
                && payload!.SlotId == field.SlotId
                && payload.TargetLevel == targetLevel);
            steps.Add(new CropShortageRecoveryStep(
                field.SlotId!.Value,
                targetLevel,
                string.IsNullOrWhiteSpace(field.Name) ? "Cropland" : field.Name,
                existing?.Id));
        }

        var production = status.ResourceStorageForecasts?
            .FirstOrDefault(forecast => string.Equals(forecast.ResourceKey, "crop", StringComparison.OrdinalIgnoreCase))
            ?.ProductionPerHour;
        return new CropShortageRecoveryPlan(
            steps,
            croplands.Count > 0 && croplands.All(field => field.Level >= maxLevel),
            activeCroplands.Count,
            production);
    }
}
