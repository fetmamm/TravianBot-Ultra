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
        new("hero_set_hide_mode", TaskGroup.Hero, "Set hero hide mode", false, TaskPayloadKind.Hero),
        new("upgrade_troops_at_smithy", TaskGroup.Troops, "Upgrade troops at smithy", true, TaskPayloadKind.None),
        new("build_troops", TaskGroup.TroopTraining, "Build troops", true, TaskPayloadKind.TroopTraining),
        new("run_brewery_celebration", TaskGroup.BreweryCelebration, "Run brewery celebration", true, TaskPayloadKind.Brewery),
        new("send_farmlists", TaskGroup.Farming, "Send farmlists", true, TaskPayloadKind.Farming),
        new("send_resources_between_villages", TaskGroup.ResourceTransfer, "Send resources between villages", true, TaskPayloadKind.ResourceTransfer),
        new("send_reinforcements_between_villages", TaskGroup.Reinforcements, "Send reinforcements between villages", true, TaskPayloadKind.Reinforcements),
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
