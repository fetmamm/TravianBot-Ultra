namespace TbotUltra.Core.Tasks;

public static class TaskCatalog
{
    private static readonly TaskDescriptor[] DescriptorsValue =
    [
        new("status", TaskGroup.Construction, "Status", false, TaskPayloadKind.None),
        new("scan_all_villages", TaskGroup.Construction, "Scan all villages", false, TaskPayloadKind.None),
        new("account_snapshot", TaskGroup.Construction, "Account snapshot", false, TaskPayloadKind.None),
        new("upgrade_resource_to_level", TaskGroup.Construction, "Upgrade resource to level", false, TaskPayloadKind.ResourceUpgrade),
        new("upgrade_all_resources_to_level", TaskGroup.Construction, "Upgrade all resources to level", false, TaskPayloadKind.ResourceUpgrade),
        new("upgrade_building_to_level", TaskGroup.Construction, "Upgrade building to level", false, TaskPayloadKind.BuildingUpgrade),
        new("upgrade_building_to_max", TaskGroup.Construction, "Upgrade building to max", false, TaskPayloadKind.BuildingUpgrade),
        new("construct_building", TaskGroup.Construction, "Construct building", false, TaskPayloadKind.BuildingConstruct),
        new("load_buildings_snapshot", TaskGroup.Construction, "Load buildings snapshot", false, TaskPayloadKind.None),
        new("demolish_building_to_level", TaskGroup.Construction, "Demolish building to level", false, TaskPayloadKind.BuildingDemolish),
        new("hero_manage", TaskGroup.Hero, "Hero manage", true, TaskPayloadKind.Hero),
        new("spend_hero_attribute_points", TaskGroup.Hero, "Spend hero attribute points", true, TaskPayloadKind.Hero),
        new("upgrade_troops_at_smithy", TaskGroup.Troops, "Upgrade troops at smithy", true, TaskPayloadKind.SmithyUpgrade),
        new("build_troops", TaskGroup.TroopTraining, "Build troops", true, TaskPayloadKind.TroopTraining),
        new("run_brewery_celebration", TaskGroup.BreweryCelebration, "Run brewery celebration", true, TaskPayloadKind.Brewery),
        new("run_town_hall_celebration", TaskGroup.TownHallCelebration, "Run Town Hall celebration", true, TaskPayloadKind.None),
        new("send_farmlists", TaskGroup.Farming, "Send farmlists", true, TaskPayloadKind.Farming),
        new("send_resources_between_villages", TaskGroup.ResourceTransfer, "Send resources between villages", true, TaskPayloadKind.ResourceTransfer),
        new("send_reinforcements_between_villages", TaskGroup.Reinforcements, "Send reinforcements between villages", true, TaskPayloadKind.Reinforcements),
        new("collect_tasks", TaskGroup.Construction, "Collect tasks", true, TaskPayloadKind.None),
        new("collect_daily_quests", TaskGroup.Construction, "Collect daily quests", true, TaskPayloadKind.None),
        new("activate_production_bonus", TaskGroup.Construction, "Activate 15% production", true, TaskPayloadKind.None),
        new("read_daily_reset", TaskGroup.Construction, "Read daily server reset time", true, TaskPayloadKind.None),
    ];

    private static readonly IReadOnlyDictionary<string, TaskDescriptor> DescriptorsByName =
        DescriptorsValue.ToDictionary(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TaskDescriptor> Descriptors => DescriptorsValue;

    public static IReadOnlyList<string> AllowedTaskNames => DescriptorsValue
        .Select(descriptor => descriptor.Name)
        .ToList();

    public static bool IsAllowed(string taskName)
    {
        return TryGetDescriptor(taskName, out _);
    }

    public static bool TryGetDescriptor(string? taskName, out TaskDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(taskName)
            && DescriptorsByName.TryGetValue(taskName.Trim(), out var match))
        {
            descriptor = match;
            return true;
        }

        descriptor = default!;
        return false;
    }
}
