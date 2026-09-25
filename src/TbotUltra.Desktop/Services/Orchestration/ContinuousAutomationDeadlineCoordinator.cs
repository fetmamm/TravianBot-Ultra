using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IContinuousAutomationDeadlinePort
{
    DateTimeOffset UtcNow { get; }
    IReadOnlyList<QueueItem> GetRelevantQueueItems();
    bool HasConsideredGroups { get; }
    DateTimeOffset? GetNextVillageStatusRoundUtc(BotOptions options);
    bool WakeWhenConstructionQueueClears { get; }
    bool? TravianPlusActive { get; }
    VillageStatus? GetBuildingStatus(QueueItem item);
    string GetVillageName(QueueItem item);
    string FormatServerTime(DateTimeOffset value);
    void LogVerbose(string message, string key);
}

internal sealed class ContinuousAutomationDeadlineCoordinator(
    IContinuousAutomationDeadlinePort port,
    AutomationPassRuntime passRuntime,
    ContinuousAutomationForecastCoordinator forecast)
{
    internal ContinuousAutomationDeadlineSnapshot Read(BotOptions options)
    {
        var now = port.UtcNow;
        var relevantItems = port.GetRelevantQueueItems();
        var nextQueueDeadline = !port.HasConsideredGroups
            ? null
            : relevantItems
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .Select(item => (DateTimeOffset?)item.NextAttemptAt)
                .Min();
        var nextVillageStatusRound = port.GetNextVillageStatusRoundUtc(options);
        var queueForecast = forecast.Resolve(now);
        var nextConstructionAvailability = queueForecast.Item?.Group == QueueGroup.Construction
            && queueForecast.State == ContinuousLoopForecastState.Waiting
                ? queueForecast.ReadyAtUtc
                : null;
        if (nextConstructionAvailability is DateTimeOffset constructionDeadline
            && (nextQueueDeadline is null || constructionDeadline < nextQueueDeadline.Value))
        {
            port.LogVerbose(
                "[loop-pick:verbose] next wake follows humanized construction availability at "
                    + $"'{port.FormatServerTime(constructionDeadline)}' "
                    + $"for {queueForecast.Item?.DisplayName ?? queueForecast.Item?.TaskName}",
                $"construction-wake:{queueForecast.Item?.Id}:{constructionDeadline.UtcTicks}");
        }

        var deadlineGroups = passRuntime.SmartSleepDeadlineGroups;
        var smartSleepItems = relevantItems
            .Where(item => deadlineGroups.Contains(item.Group))
            .ToList();
        var overrides = ResolveQueueDeadlineOverrides(smartSleepItems, now);
        var smartSleepForecast = forecast.Resolve(
            now,
            queueItemsOverride: smartSleepItems,
            wakeWhenConstructionQueueClears: port.WakeWhenConstructionQueueClears);
        var smartSleepConstructionAvailability = smartSleepForecast.Item?.Group == QueueGroup.Construction
            && smartSleepForecast.State == ContinuousLoopForecastState.Waiting
                ? smartSleepForecast.ReadyAtUtc
                : null;

        return new ContinuousAutomationDeadlineSnapshot(
            nextQueueDeadline,
            nextConstructionAvailability,
            nextVillageStatusRound,
            smartSleepItems,
            deadlineGroups,
            smartSleepConstructionAvailability,
            overrides);
    }

    internal IReadOnlyDictionary<Guid, DateTimeOffset> ResolveQueueDeadlineOverrides(
        IReadOnlyList<QueueItem> items,
        DateTimeOffset now)
    {
        var overrides = new Dictionary<Guid, DateTimeOffset>();
        var candidates = new List<(QueueItem Item, DateTimeOffset Deadline)>();
        if (!port.WakeWhenConstructionQueueClears)
        {
            return overrides;
        }

        foreach (var item in items.Where(item =>
                     item.Status == QueueStatus.Pending
                     && item.Group == QueueGroup.Construction))
        {
            var queueClearDelay = ConstructionQueueState.ResolveSmartSleepQueueClearDelay(
                port.GetBuildingStatus(item),
                port.TravianPlusActive,
                item,
                now);
            if (queueClearDelay is not { } delay || delay <= TimeSpan.Zero)
            {
                continue;
            }

            var queueClearDeadline = now.Add(delay);
            var effectiveDeadline = item.NextAttemptAt > queueClearDeadline
                ? item.NextAttemptAt
                : queueClearDeadline;
            overrides[item.Id] = effectiveDeadline;
            candidates.Add((item, effectiveDeadline));
        }

        if (candidates.Count > 0)
        {
            var selected = candidates
                .OrderBy(candidate => candidate.Deadline)
                .ThenBy(candidate => candidate.Item.Id)
                .First();
            var distinctDeadlineCount = candidates
                .Select(candidate => candidate.Deadline)
                .Distinct()
                .Count();
            port.LogVerbose(
                $"[smart-sleep] construction deadline summary: candidates={candidates.Count}, "
                    + $"distinctDeadlines={distinctDeadlineCount}, selected='{port.FormatServerTime(selected.Deadline)}', "
                    + $"task='{selected.Item.DisplayName ?? selected.Item.TaskName}', "
                    + $"village='{port.GetVillageName(selected.Item)}', itemId={selected.Item.Id}, mode=queue-clear.",
                $"smart-sleep:construction-summary:{selected.Item.Id}:{selected.Deadline.UtcTicks}:"
                    + $"{candidates.Count}:{distinctDeadlineCount}");
        }

        return overrides;
    }
}
