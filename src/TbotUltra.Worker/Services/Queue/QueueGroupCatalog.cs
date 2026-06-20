using TbotUltra.Core.Tasks;
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
            [QueueGroup.BreweryCelebration] = ("brewery_celebration", "Brewery Celebration", "Teutons brewery celebration."),
            [QueueGroup.NpcTrade] = ("npc_trade", "NPC Trade", "NPC resource exchange while building troops, buildings, or resource fields."),
            [QueueGroup.ResourceTransfer] = ("resource_transfer", "Resource Transfer", "Send resources between own villages."),
            [QueueGroup.Reinforcements] = ("reinforcements", "Reinforcements", "Send troops between own villages."),
        };

    public static IReadOnlyList<QueueGroup> AllGroups => Metadata.Keys.ToList();

    public static QueueGroup ResolveGroup(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return QueueGroup.Construction;
        }

        if (taskName.StartsWith("desktop_runtime_manual:farm", StringComparison.OrdinalIgnoreCase)
            || taskName.StartsWith("desktop_runtime_manual:analyze_farmlists", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGroup.Farming;
        }

        if (taskName.StartsWith("desktop_runtime_manual:hero", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGroup.Hero;
        }

        if (TbotUltra.Core.Tasks.TaskCatalog.TryGetDescriptor(taskName, out var descriptor))
        {
            return ToQueueGroup(descriptor.Group);
        }

        return QueueGroup.Construction;
    }

    private static QueueGroup ToQueueGroup(TaskGroup group)
    {
        return group switch
        {
            TaskGroup.Troops => QueueGroup.Troops,
            TaskGroup.Hero => QueueGroup.Hero,
            TaskGroup.Farming => QueueGroup.Farming,
            TaskGroup.TroopTraining => QueueGroup.TroopTraining,
            TaskGroup.BreweryCelebration => QueueGroup.BreweryCelebration,
            TaskGroup.NpcTrade => QueueGroup.NpcTrade,
            TaskGroup.ResourceTransfer => QueueGroup.ResourceTransfer,
            TaskGroup.Reinforcements => QueueGroup.Reinforcements,
            _ => QueueGroup.Construction,
        };
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
