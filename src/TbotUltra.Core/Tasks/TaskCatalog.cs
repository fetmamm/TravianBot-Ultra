namespace TbotUltra.Core.Tasks;

public static class TaskCatalog
{
    private static readonly string[] AllowedTaskNamesValue =
    [
        "status",
        "scan_all_villages",
        "account_snapshot",
        "upgrade_resource_to_level",
        "upgrade_all_resources_to_level",
        "upgrade_building_to_level",
        "upgrade_building_to_max",
        "construct_building",
        "load_buildings_snapshot",
        "demolish_building_to_level",
        "hero_manage",
        "hero_set_hide_mode",
        "upgrade_troops_at_smithy",
    ];

    private static readonly HashSet<string> AllowedSet = new(AllowedTaskNamesValue, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AllowedTaskNames => AllowedTaskNamesValue;

    public static bool IsAllowed(string taskName)
    {
        return AllowedSet.Contains(taskName ?? string.Empty);
    }
}
