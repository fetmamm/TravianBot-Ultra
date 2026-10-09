using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

/// <summary>Normalizes the account's village task group order, including old Dashboard-only values.</summary>
internal static class VillageTaskPriorityOrder
{
    internal static IReadOnlyList<string> DefaultKeys { get; } =
    [
        QueueGroupCatalog.GetKey(QueueGroup.Hero),
        QueueGroupCatalog.GetKey(QueueGroup.Construction),
        QueueGroupCatalog.GetKey(QueueGroup.Demolish),
        QueueGroupCatalog.GetKey(QueueGroup.Troops),
        QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration),
        QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration),
        QueueGroupCatalog.GetKey(QueueGroup.Farming),
        QueueGroupCatalog.GetKey(QueueGroup.TroopTraining),
        QueueGroupCatalog.GetKey(QueueGroup.ResourceTransfer),
        QueueGroupCatalog.GetKey(QueueGroup.NpcTrade),
        QueueGroupCatalog.GetKey(QueueGroup.Reinforcements),
    ];

    // Earlier releases persisted the Dashboard card order on toggle changes. NPC Trade had no card,
    // so both the full legacy default and the card-only version must count as an untouched default.
    private static readonly IReadOnlyList<string> LegacyDefaultKeys =
    [
        QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration),
        QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration),
        QueueGroupCatalog.GetKey(QueueGroup.Hero),
        QueueGroupCatalog.GetKey(QueueGroup.Construction),
        QueueGroupCatalog.GetKey(QueueGroup.Troops),
        QueueGroupCatalog.GetKey(QueueGroup.Farming),
        QueueGroupCatalog.GetKey(QueueGroup.TroopTraining),
        QueueGroupCatalog.GetKey(QueueGroup.ResourceTransfer),
        QueueGroupCatalog.GetKey(QueueGroup.NpcTrade),
        QueueGroupCatalog.GetKey(QueueGroup.Reinforcements),
        QueueGroupCatalog.GetKey(QueueGroup.Demolish),
    ];

    internal static IReadOnlyList<string> Resolve(IEnumerable<string>? configured)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in configured ?? [])
        {
            if (!QueueGroupCatalog.TryParse(value?.Trim(), out var group) || group == QueueGroup.Account)
            {
                continue;
            }

            var key = QueueGroupCatalog.GetKey(group);
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0
            || keys.SequenceEqual(LegacyDefaultKeys, StringComparer.OrdinalIgnoreCase)
            || keys.SequenceEqual(LegacyDefaultKeys.Where(key => key != QueueGroupCatalog.GetKey(QueueGroup.NpcTrade)),
                StringComparer.OrdinalIgnoreCase))
        {
            return DefaultKeys.ToList();
        }

        // Preserve a custom order. Insert newly added or formerly card-less groups next to their
        // closest default-order successor rather than moving the user's existing choices.
        foreach (var missing in DefaultKeys.Where(key => !keys.Contains(key, StringComparer.OrdinalIgnoreCase)))
        {
            var next = DefaultKeys.SkipWhile(key => !string.Equals(key, missing, StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault(key => keys.Contains(key, StringComparer.OrdinalIgnoreCase));
            var index = next is null ? keys.Count : keys.FindIndex(key => string.Equals(key, next, StringComparison.OrdinalIgnoreCase));
            keys.Insert(index, missing);
        }

        return keys;
    }
}
