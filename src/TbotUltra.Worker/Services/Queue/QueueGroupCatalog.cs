using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public static class QueueGroupCatalog
{
    private static readonly IReadOnlyDictionary<QueueGroup, (string Key, string Title, string Description)> Metadata =
        new Dictionary<QueueGroup, (string Key, string Title, string Description)>
        {
            [QueueGroup.Construction] = ("construction", "Construction", "Resources and buildings."),
            [QueueGroup.Troops] = ("troops", "Upgrade Troops", "Smithy and troop tasks."),
            [QueueGroup.Hero] = ("hero", "Hero", "Hero actions and adventures."),
            [QueueGroup.Farming] = ("farming", "Farming", "Selected farmlists."),
            [QueueGroup.TroopTraining] = ("troop_training", "Build Troops", "Barracks, Stable, and Workshop."),
        };

    public static IReadOnlyList<QueueGroup> AllGroups => Metadata.Keys.ToList();

    public static QueueGroup ResolveGroup(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return QueueGroup.Construction;
        }

        if (taskName.StartsWith("desktop_runtime_manual:farm", StringComparison.OrdinalIgnoreCase)
            || taskName.StartsWith("desktop_runtime_manual:analyze_farmlists", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "send_farmlists", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGroup.Farming;
        }

        if (taskName.StartsWith("desktop_runtime_manual:hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "hero_manage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "hero_set_hide_mode", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGroup.Hero;
        }

        if (string.Equals(taskName, "upgrade_troops_at_smithy", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGroup.Troops;
        }

        if (string.Equals(taskName, "build_troops", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGroup.TroopTraining;
        }

        return QueueGroup.Construction;
    }

    public static string GetKey(QueueGroup group) => Metadata[group].Key;

    public static string GetTitle(QueueGroup group) => Metadata[group].Title;

    public static string GetDescription(QueueGroup group) => Metadata[group].Description;

    public static bool TryParse(string? value, out QueueGroup group)
    {
        foreach (var pair in Metadata)
        {
            if (string.Equals(pair.Value.Key, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                group = pair.Key;
                return true;
            }
        }

        group = QueueGroup.Construction;
        return false;
    }
}
