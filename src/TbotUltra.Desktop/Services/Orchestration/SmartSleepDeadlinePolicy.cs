using System.Text.Json.Nodes;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal static class SmartSleepDeadlinePolicy
{
    internal static IReadOnlyList<QueueGroup> AllGroups { get; } =
    [
        QueueGroup.Construction,
        QueueGroup.Hero,
        QueueGroup.Farming,
        QueueGroup.TroopTraining,
        QueueGroup.Troops,
        QueueGroup.Demolish,
        QueueGroup.BreweryCelebration,
        QueueGroup.NpcTrade,
        QueueGroup.ResourceTransfer,
        QueueGroup.Reinforcements,
        QueueGroup.TownHallCelebration,
        QueueGroup.Account,
    ];

    internal static IReadOnlySet<QueueGroup> DefaultGroups { get; } =
        new HashSet<QueueGroup> { QueueGroup.Construction, QueueGroup.Hero };

    internal static HashSet<QueueGroup> ReadGroups(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return DefaultGroups.ToHashSet();
        }

        var groups = new HashSet<QueueGroup>();
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var key)
                || !QueueGroupCatalog.TryParse(key, out var group))
            {
                return DefaultGroups.ToHashSet();
            }

            groups.Add(group);
        }

        return groups;
    }

    internal static TimeSpan? ResolveNextDelay(
        DateTimeOffset now,
        IEnumerable<QueueItem> queueItems,
        IReadOnlySet<QueueGroup> deadlineGroups,
        DateTimeOffset? nextConstructionAvailabilityUtc)
    {
        var nextQueueDeadline = queueItems
            .Where(item => item.Status == QueueStatus.Pending
                && item.NextAttemptAt > now
                && deadlineGroups.Contains(item.Group))
            .Select(item => (DateTimeOffset?)item.NextAttemptAt)
            .Min();
        var constructionDeadline = deadlineGroups.Contains(QueueGroup.Construction)
            ? nextConstructionAvailabilityUtc
            : null;

        return AutomationDeadlinePolicy.ResolveNextDelay(
            now,
            nextQueueDeadline,
            constructionDeadline,
            nextVillageStatusRoundUtc: null);
    }
}
