using System.Text.Json;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteRunBreweryCelebrationAsync(TaskExecutionContext context)
    {
        context.Log("[brewery] run_brewery_celebration starting");
        var result = await context.Client.RunBreweryCelebrationAsync(context.Options.BreweryCelebrationRestartDelayEnabled, context.Options.BreweryCelebrationRestartDelayMinMinutes, context.Options.BreweryCelebrationRestartDelayMaxMinutes, context.CancellationToken);
        context.Log(result);
        ThrowIfTaskBlocked("run_brewery_celebration", result);
    }

    private static async Task ExecuteRunTownHallCelebrationAsync(TaskExecutionContext context)
    {
        var mode = TownHallCelebrationDefaults.NormalizeMode(context.Options.TownHallCelebrationMode);
        var count = TownHallCelebrationDefaults.NormalizeCount(context.Options.TownHallCelebrationCount);
        context.Log($"[town-hall] run_town_hall_celebration starting mode={mode} count={count}");
        var result = await context.Client.RunTownHallCelebrationAsync(mode, count, context.Options.TownHallCelebrationRestartDelayEnabled, context.Options.TownHallCelebrationRestartDelayMinMinutes, context.Options.TownHallCelebrationRestartDelayMaxMinutes, context.CancellationToken);
        context.Log(result);
        if (result.Contains("town_hall_unavailable=missing", StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskBlockedPermanentlyException($"Task 'run_town_hall_celebration' blocked permanently: town_hall_unavailable=missing | {result}");
        }
        ThrowIfTaskBlocked("run_town_hall_celebration", result);
    }

    private static async Task ExecuteLoadBuildingsSnapshotAsync(TaskExecutionContext context)
    {
        var status = await context.Client.ReadVillageStatusAsync(context.CancellationToken);
        await WriteBuildingsSnapshotAsync(context, status);
        context.Log($"Loaded {status.Buildings.Count} building slots.");
    }

    private static async Task WriteBuildingsSnapshotAsync(TaskExecutionContext context, TbotUltra.Worker.Domain.VillageStatus status)
    {
        var activeAccount = context.Runner._accountProvider.LoadAccount().Name;
        var safeAccount = string.IsNullOrWhiteSpace(activeAccount) ? "main" : activeAccount.Trim().ToLowerInvariant();
        var outputDir = Path.Combine(context.Runner._projectContext.RootPath, "temp_build_out", "buildings-snapshots");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{safeAccount}.json");
        var payload = new
        {
            account = activeAccount, status.ActiveVillage, status.Tribe, status.IsCapital, status.WarehouseCapacity, status.GranaryCapacity,
            buildings = status.Buildings.Select(building => new { building.SlotId, building.Name, building.Level, building.Url, building.Gid }).ToList(),
            resourceFields = status.ResourceFields.Select(field => new { field.SlotId, field.FieldType, field.Name, field.Level, field.Url }).ToList(),
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload), context.CancellationToken);
    }

    private static async Task RefreshBuildingsSnapshotAfterTaskAsync(TaskExecutionContext context)
    {
        try
        {
            var status = await context.Client.ReadBuildingsStatusAsync(context.CancellationToken);
            await WriteBuildingsSnapshotAsync(context, status);
            context.Log($"Buildings snapshot refreshed ({status.Buildings.Count} slots).");
        }
        catch (Exception ex)
        {
            context.Log($"Could not refresh buildings snapshot: {ex.Message}");
        }
    }

    private static async Task ExecuteDemolishBuildingToLevelAsync(TaskExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Options.TargetBuildingSlotOrName) || context.Options.TargetLevel is null)
        {
            context.Log("Task 'demolish_building_to_level' requires config values target_building_slot_or_name and target_level.");
            return;
        }
        var result = await context.Client.DemolishBuildingToLevelAsync(context.Options.TargetBuildingSlotOrName, context.Options.TargetLevel.Value, context.CancellationToken);
        context.Log(result);
    }

    private static async Task ExecuteUpgradeResourceToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.ResourceUpgradeSlotId is null || context.Options.ResourceUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_resource_to_level' requires config values resource_upgrade_slot_id and resource_upgrade_target_level.");
            return;
        }
        var result = await context.Client.UpgradeResourceToLevelAsync(context.Options.ResourceUpgradeSlotId.Value, context.Options.ResourceUpgradeTargetLevel.Value, context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_resource_to_level", result);
        ThrowIfTaskBlocked("upgrade_resource_to_level", result);
    }

    private static async Task ExecuteUpgradeAllResourcesToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.ResourceUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_all_resources_to_level' requires config value resource_upgrade_target_level.");
            return;
        }
        var result = await context.Client.UpgradeAllResourcesToLevelAsync(context.Options.ResourceUpgradeTargetLevel.Value, context.Options.ResourceBuildStrategy, context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_all_resources_to_level", result);
        ThrowIfTaskBlocked("upgrade_all_resources_to_level", result);
    }

    private static async Task ExecuteUpgradeBuildingToLevelAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingUpgradeSlotId is null || context.Options.BuildingUpgradeTargetLevel is null)
        {
            context.Log("Task 'upgrade_building_to_level' requires config values building_upgrade_slot_id and building_upgrade_target_level.");
            return;
        }
        var result = await context.Client.UpgradeBuildingToLevelAsync(context.Options.BuildingUpgradeSlotId.Value, context.Options.BuildingUpgradeTargetLevel.Value, context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_building_to_level", result);
        ThrowIfTaskBlocked("upgrade_building_to_level", result);
    }

    private static async Task ExecuteUpgradeBuildingToMaxAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingUpgradeSlotId is null)
        {
            context.Log("Task 'upgrade_building_to_max' requires config value building_upgrade_slot_id.");
            return;
        }
        var result = await context.Client.UpgradeBuildingToMaxAsync(context.Options.BuildingUpgradeSlotId.Value, context.Options.BuildingUpgradeMaxAttempts, context.CancellationToken);
        context.Log(result);
        context.RecordTaskResult("upgrade_building_to_max", result);
        ThrowIfTaskBlocked("upgrade_building_to_max", result);
    }

    private static async Task ExecuteConstructBuildingAsync(TaskExecutionContext context)
    {
        if (context.Options.BuildingConstructSlotId is null || context.Options.BuildingConstructGid is null)
        {
            context.Log("Task 'construct_building' requires config values building_construct_slot_id and building_construct_gid.");
            return;
        }
        var buildingName = string.IsNullOrWhiteSpace(context.Options.BuildingConstructName) ? $"gid {context.Options.BuildingConstructGid.Value}" : context.Options.BuildingConstructName;
        var result = await context.Client.ConstructBuildingAsync(context.Options.BuildingConstructSlotId.Value, context.Options.BuildingConstructGid.Value, buildingName, context.CancellationToken, context.Options.BuildingConstructAllowSlotFallback, context.Options.BuildingConstructFallbackExcludedSlots);
        context.Log(result);
        context.RecordTaskResult("construct_building", result);
        ThrowIfTaskBlocked("construct_building", result);
    }
}
