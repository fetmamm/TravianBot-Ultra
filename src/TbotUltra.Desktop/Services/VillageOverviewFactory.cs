using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

internal static class VillageOverviewFactory
{
    private const int ProjectionLimit = 5;

    internal static VillageOverviewSnapshot Create(
        IReadOnlyList<VillageOverviewSource> villages,
        IReadOnlyList<PipelineTaskSource> tasks,
        IReadOnlyList<QueueGroup> orderedGroups,
        string? activeVillageKey,
        QueueItem? exactNext,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string> finishTimeFormatter,
        IReadOnlyDictionary<QueueGroup, string?>? rotationVillageKeys = null)
    {
        var taskSnapshot = tasks
            .Where(source => source.Item is not null)
            .ToList();
        var running = taskSnapshot.FirstOrDefault(source => source.Item.Status == QueueStatus.Running);
        var upcoming = BuildUpcomingTasks(
            taskSnapshot,
            orderedGroups,
            activeVillageKey,
            exactNext,
            nowUtc,
            rotationVillageKeys);
        var knownVillageKeys = villages
            .Select(village => village.VillageKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = villages
            .Select(village => BuildVillageRow(
                village,
                ResolveVillageTasks(village, taskSnapshot, knownVillageKeys),
                orderedGroups,
                exactNext,
                nowUtc,
                finishTimeFormatter))
            .ToList();

        return new VillageOverviewSnapshot(
            running is null ? "Nothing running" : DescribeTask(running),
            upcoming,
            rows,
            nowUtc);
    }

    private static IReadOnlyList<UpcomingTaskRow> BuildUpcomingTasks(
        IReadOnlyList<PipelineTaskSource> tasks,
        IReadOnlyList<QueueGroup> orderedGroups,
        string? activeVillageKey,
        QueueItem? exactNext,
        DateTimeOffset nowUtc,
        IReadOnlyDictionary<QueueGroup, string?>? rotationVillageKeys)
    {
        var remaining = tasks
            .Where(source => source.IsAllowed
                && source.Item.Status is QueueStatus.Pending or QueueStatus.Paused)
            .OrderBy(source => source.Item.IsRuntimeOnly)
            .ThenByDescending(source => source.Item.Priority)
            .ThenBy(source => source.Item.CreatedAt)
            .ToList();
        var result = new List<UpcomingTaskRow>(ProjectionLimit);
        var projectedVillageKey = activeVillageKey;
        var projectedRotations = rotationVillageKeys is null
            ? new Dictionary<QueueGroup, string?>()
            : new Dictionary<QueueGroup, string?>(rotationVillageKeys);

        if (exactNext is not null)
        {
            var exact = remaining.FirstOrDefault(source => source.Item.Id == exactNext.Id)
                ?? tasks.FirstOrDefault(source => source.Item.Id == exactNext.Id);
            if (exact is not null)
            {
                result.Add(ToUpcomingRow(exact, 1, nowUtc, isExact: true, waitsForPrevious: false));
                remaining.RemoveAll(source => source.Item.Id == exactNext.Id);
                projectedVillageKey = exact.VillageKey ?? projectedVillageKey;
                projectedRotations[exact.Item.Group] = exact.VillageKey;
            }
        }

        while (result.Count < ProjectionLimit && remaining.Count > 0)
        {
            var selected = SelectProjectedTask(
                remaining,
                orderedGroups,
                projectedVillageKey,
                projectedRotations,
                nowUtc);
            if (selected is null)
            {
                selected = SelectEarliestDeferredHead(remaining, orderedGroups);
            }

            if (selected is null)
            {
                var blocked = SelectBlockedHead(remaining, orderedGroups);
                if (blocked is null)
                {
                    break;
                }

                result.Add(ToUpcomingRow(blocked, result.Count + 1, nowUtc, isExact: false, waitsForPrevious: false));
                break;
            }

            result.Add(ToUpcomingRow(
                selected,
                result.Count + 1,
                nowUtc,
                isExact: false,
                waitsForPrevious: result.Count > 0 && selected.Item.NextAttemptAt <= nowUtc));
            remaining.RemoveAll(source => source.Item.Id == selected.Item.Id);
            projectedVillageKey = selected.VillageKey ?? projectedVillageKey;
            projectedRotations[selected.Item.Group] = selected.VillageKey;
        }

        return result;
    }

    private static PipelineTaskSource? SelectProjectedTask(
        IReadOnlyList<PipelineTaskSource> remaining,
        IReadOnlyList<QueueGroup> orderedGroups,
        string? activeVillageKey,
        IReadOnlyDictionary<QueueGroup, string?> rotationVillageKeys,
        DateTimeOffset nowUtc)
    {
        if (!string.IsNullOrWhiteSpace(activeVillageKey))
        {
            foreach (var group in EnumerateGroups(orderedGroups, remaining))
            {
                var head = HeadForVillageAndGroup(remaining, activeVillageKey, group);
                if (IsReady(head, nowUtc))
                {
                    return head;
                }
            }
        }

        foreach (var group in EnumerateGroups(orderedGroups, remaining))
        {
            var villageHeads = remaining
                .Where(source => source.Item.Group == group)
                .GroupBy(source => source.VillageKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(grouping => grouping.First())
                .ToList();
            if (rotationVillageKeys.TryGetValue(group, out var rotationKey)
                && !string.IsNullOrWhiteSpace(rotationKey))
            {
                var sticky = villageHeads.FirstOrDefault(head =>
                    string.Equals(head.VillageKey, rotationKey, StringComparison.OrdinalIgnoreCase)
                    && IsReady(head, nowUtc));
                if (sticky is not null)
                {
                    return sticky;
                }
            }

            var ready = villageHeads.FirstOrDefault(head => IsReady(head, nowUtc));
            if (ready is not null)
            {
                return ready;
            }
        }

        return null;
    }

    private static PipelineTaskSource? SelectEarliestDeferredHead(
        IReadOnlyList<PipelineTaskSource> remaining,
        IReadOnlyList<QueueGroup> orderedGroups)
    {
        return EnumerateGroups(orderedGroups, remaining)
            .SelectMany(group => remaining
                .Where(source => source.Item.Group == group)
                .GroupBy(source => source.VillageKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(grouping => grouping.First()))
            .Where(source => source.Item.Status == QueueStatus.Pending)
            .OrderBy(source => source.Item.NextAttemptAt)
            .FirstOrDefault();
    }

    private static PipelineTaskSource? SelectBlockedHead(
        IReadOnlyList<PipelineTaskSource> remaining,
        IReadOnlyList<QueueGroup> orderedGroups)
    {
        return EnumerateGroups(orderedGroups, remaining)
            .SelectMany(group => remaining
                .Where(source => source.Item.Group == group)
                .GroupBy(source => source.VillageKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(grouping => grouping.First()))
            .FirstOrDefault(source => source.Item.Status == QueueStatus.Paused);
    }

    private static PipelineTaskSource? HeadForVillageAndGroup(
        IEnumerable<PipelineTaskSource> remaining,
        string villageKey,
        QueueGroup group)
    {
        return remaining.FirstOrDefault(source =>
            source.Item.Group == group
            && string.Equals(source.VillageKey, villageKey, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReady(PipelineTaskSource? source, DateTimeOffset nowUtc)
        => source is not null
            && source.Item.Status == QueueStatus.Pending
            && source.Item.NextAttemptAt <= nowUtc;

    private static IEnumerable<QueueGroup> EnumerateGroups(
        IReadOnlyList<QueueGroup> orderedGroups,
        IEnumerable<PipelineTaskSource> tasks)
    {
        var seen = new HashSet<QueueGroup>();
        foreach (var group in orderedGroups)
        {
            if (seen.Add(group))
            {
                yield return group;
            }
        }

        foreach (var group in tasks.Select(source => source.Item.Group))
        {
            if (seen.Add(group))
            {
                yield return group;
            }
        }
    }

    private static UpcomingTaskRow ToUpcomingRow(
        PipelineTaskSource source,
        int position,
        DateTimeOffset nowUtc,
        bool isExact,
        bool waitsForPrevious)
    {
        string timing;
        if (source.Item.Status == QueueStatus.Paused)
        {
            timing = "Blocked (paused queue head)";
        }
        else if (source.Item.NextAttemptAt > nowUtc)
        {
            timing = $"Earliest in {FormatCountdown(source.Item.NextAttemptAt - nowUtc)}";
        }
        else if (isExact)
        {
            timing = "Now";
        }
        else
        {
            timing = waitsForPrevious ? "Ready (after previous task)" : "Ready";
        }

        return new UpcomingTaskRow(
            position.ToString(),
            source.DisplayName,
            string.IsNullOrWhiteSpace(source.VillageName) ? "Account-wide" : source.VillageName,
            QueueGroupCatalog.GetTitle(source.Item.Group),
            timing,
            isExact ? "Exact next" : "Projection");
    }

    // Tasks that belong to a village. Primary match is the stable key, but a task whose key matches NO known
    // village is attributed by its (resolved) display name. This keeps the per-village "Next task" consistent
    // with the Upcoming tasks list: a task can carry a coordinate key (xy:) while the dashboard row for the
    // same village only resolves to a name key (name:) when the settings store has not learned its
    // coordinates, so a key-only join silently drops it and the row wrongly reads "Nothing queued". The name
    // fallback is gated on "matches no village key" so two villages sharing a name never steal keyed tasks.
    private static List<PipelineTaskSource> ResolveVillageTasks(
        VillageOverviewSource village,
        IReadOnlyList<PipelineTaskSource> tasks,
        IReadOnlySet<string> knownVillageKeys)
    {
        return tasks
            .Where(source =>
            {
                if (!string.IsNullOrWhiteSpace(source.VillageKey)
                    && string.Equals(source.VillageKey, village.VillageKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var keyMatchesSomeVillage = !string.IsNullOrWhiteSpace(source.VillageKey)
                    && knownVillageKeys.Contains(source.VillageKey);
                return !keyMatchesSomeVillage
                    && !string.IsNullOrWhiteSpace(source.VillageName)
                    && string.Equals(source.VillageName, village.Name, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    private static VillageOverviewRow BuildVillageRow(
        VillageOverviewSource village,
        IReadOnlyList<PipelineTaskSource> villageTasks,
        IReadOnlyList<QueueGroup> orderedGroups,
        QueueItem? exactNext,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string> finishTimeFormatter)
    {
        var constructionEnabled = IsGroupEnabled(village, QueueGroup.Construction);
        var smithyEnabled = IsGroupEnabled(village, QueueGroup.Troops);
        var troopTrainingEnabled = IsGroupEnabled(village, QueueGroup.TroopTraining);
        var farmingEnabled = IsGroupEnabled(village, QueueGroup.Farming);
        var heroEnabled = IsGroupEnabled(village, QueueGroup.Hero);
        var townHallEnabled = IsGroupEnabled(village, QueueGroup.TownHallCelebration);
        var breweryEnabled = IsGroupEnabled(village, QueueGroup.BreweryCelebration);

        return new VillageOverviewRow(
            village.Name,
            village.Population,
            ResolveNextTask(village, villageTasks, orderedGroups, exactNext, nowUtc),
            ResolveConstruction(village.Status, village.Tribe, villageTasks, constructionEnabled, nowUtc, finishTimeFormatter),
            ResolveSmithy(village.Status, villageTasks, smithyEnabled, nowUtc, finishTimeFormatter),
            ResolveTroopTraining(village.Status, villageTasks, troopTrainingEnabled, nowUtc),
            ResolveFarming(village.Status, villageTasks, farmingEnabled, nowUtc),
            ResolveHero(village, heroEnabled, nowUtc),
            ResolveTownHall(village, villageTasks, townHallEnabled, nowUtc),
            ResolveBrewery(village.Status, villageTasks, breweryEnabled, nowUtc),
            ResolveQueueGroupStatus(villageTasks, QueueGroup.ResourceTransfer, IsGroupEnabled(village, QueueGroup.ResourceTransfer), nowUtc, "Ready"),
            ResolveQueueGroupStatus(villageTasks, QueueGroup.Reinforcements, IsGroupEnabled(village, QueueGroup.Reinforcements), nowUtc, "Ready"));
    }

    private static string ResolveNextTask(
        VillageOverviewSource village,
        IReadOnlyList<PipelineTaskSource> villageTasks,
        IReadOnlyList<QueueGroup> orderedGroups,
        QueueItem? exactNext,
        DateTimeOffset nowUtc)
    {
        if (!village.IsEnabled)
        {
            return "Disabled";
        }

        var running = villageTasks.FirstOrDefault(source => source.Item.Status == QueueStatus.Running);
        if (running is not null)
        {
            return $"Running: {running.DisplayName}";
        }

        var exact = exactNext is null
            ? null
            : villageTasks.FirstOrDefault(source => source.Item.Id == exactNext.Id);
        if (exact is not null)
        {
            return $"Next: {exact.DisplayName}";
        }

        var orderedHeads = EnumerateGroups(orderedGroups, villageTasks)
            .Select(group => villageTasks
                .Where(source => source.IsAllowed && source.Item.Group == group)
                .OrderBy(source => source.Item.IsRuntimeOnly)
                .ThenByDescending(source => source.Item.Priority)
                .ThenBy(source => source.Item.CreatedAt)
                .FirstOrDefault())
            .Where(source => source is not null)
            .Cast<PipelineTaskSource>()
            .ToList();
        var ready = orderedHeads.FirstOrDefault(source =>
            source.Item.Status == QueueStatus.Pending && source.Item.NextAttemptAt <= nowUtc);
        if (ready is not null)
        {
            return $"Ready: {ready.DisplayName}";
        }

        var deferred = orderedHeads
            .Where(source => source.Item.Status == QueueStatus.Pending && source.Item.NextAttemptAt > nowUtc)
            .OrderBy(source => source.Item.NextAttemptAt)
            .FirstOrDefault();
        return deferred is null
            ? "Nothing queued"
            : $"Waiting {FormatCountdown(deferred.Item.NextAttemptAt - nowUtc)}: {deferred.DisplayName}";
    }

    private static string ResolveConstruction(
        VillageStatus? status,
        string tribe,
        IReadOnlyList<PipelineTaskSource> tasks,
        bool enabled,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string> finishTimeFormatter)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        var active = ConstructionQueueState.ResolveCurrentActiveConstructions(status, nowUtc);
        if (active.Count == 0 && HasQueueGroupWork(tasks, QueueGroup.Construction))
        {
            return ResolveQueueGroupStatus(tasks, QueueGroup.Construction, enabled, nowUtc, "Ready");
        }

        var snapshot = ConstructionQueueState.ResolveSnapshot(status, nowUtc);
        var hasStatus = snapshot.Knowledge != ConstructionQueueKnowledge.Unknown;
        var slotCount = tribe.Contains("Roman", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        var rows = LiveQueueRowFactory.BuildConstructionRows(
            active,
            slotCount,
            hasStatus,
            nowUtc,
            finishTimeFormatter);
        return string.Join("\n", rows.Select(row => FormatQueueRow(row.Name, row.LevelText, row.CountdownText)));
    }

    private static string ResolveSmithy(
        VillageStatus? status,
        IReadOnlyList<PipelineTaskSource> tasks,
        bool enabled,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string> finishTimeFormatter)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        var active = SmithyQueueState.ResolveActiveUpgrades(status?.SmithyUpgradeStatus, nowUtc);
        if (active.Count == 0 && HasQueueGroupWork(tasks, QueueGroup.Troops))
        {
            return ResolveQueueGroupStatus(tasks, QueueGroup.Troops, enabled, nowUtc, "Ready");
        }

        var rows = LiveQueueRowFactory.BuildSmithyRows(
            active,
            2,
            status?.SmithyUpgradeStatus is not null,
            nowUtc,
            finishTimeFormatter);
        return string.Join("\n", rows.Select(row => FormatQueueRow(row.Name, row.LevelText, row.CountdownText)));
    }

    private static string ResolveTroopTraining(
        VillageStatus? status,
        IReadOnlyList<PipelineTaskSource> tasks,
        bool enabled,
        DateTimeOffset nowUtc)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        if (status?.TroopTrainingQueues is null)
        {
            return HasQueueGroupWork(tasks, QueueGroup.TroopTraining)
                ? ResolveQueueGroupStatus(tasks, QueueGroup.TroopTraining, enabled, nowUtc, "Ready")
                : "Not loaded";
        }

        var queues = status.TroopTrainingQueues
            .Where(queue => queue.Exists)
            .Select(queue =>
            {
                var remaining = queue.Finish?.RemainingSecondsAt(nowUtc) ?? queue.RemainingSeconds;
                return $"{queue.BuildingName}: {(remaining is > 0 ? FormatCountdown(TimeSpan.FromSeconds(remaining.Value)) : "Ready")}";
            })
            .ToList();
        if (!queues.Any(line => !line.EndsWith(": Ready", StringComparison.Ordinal))
            && HasQueueGroupWork(tasks, QueueGroup.TroopTraining))
        {
            return ResolveQueueGroupStatus(tasks, QueueGroup.TroopTraining, enabled, nowUtc, "Ready");
        }

        return queues.Count == 0 ? "Not available" : string.Join("\n", queues);
    }

    private static string ResolveFarming(
        VillageStatus? status,
        IReadOnlyList<PipelineTaskSource> tasks,
        bool enabled,
        DateTimeOffset nowUtc)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        if (status?.FarmLists is null)
        {
            return HasQueueGroupWork(tasks, QueueGroup.Farming)
                ? ResolveQueueGroupStatus(tasks, QueueGroup.Farming, enabled, nowUtc, "Ready")
                : "Not loaded";
        }

        if (status.FarmLists.Count == 0)
        {
            return "Not available";
        }

        var active = status.FarmLists.Where(list =>
            (list.Finish?.RemainingSecondsAt(nowUtc) ?? list.RemainingSeconds) is > 0).ToList();
        if (active.Count == 0 && HasQueueGroupWork(tasks, QueueGroup.Farming))
        {
            return ResolveQueueGroupStatus(tasks, QueueGroup.Farming, enabled, nowUtc, "Ready");
        }

        return string.Join("\n", status.FarmLists.Select(list =>
        {
            var remaining = list.Finish?.RemainingSecondsAt(nowUtc) ?? list.RemainingSeconds;
            var timer = remaining is > 0 ? FormatCountdown(TimeSpan.FromSeconds(remaining.Value)) : "Ready";
            return $"{list.Name}: {timer}{(list.TimerIsEstimated ? " (estimated)" : string.Empty)}";
        }));
    }

    private static string ResolveHero(VillageOverviewSource village, bool enabled, DateTimeOffset nowUtc)
    {
        if (!village.IsHeroHome)
        {
            return "Not applicable";
        }

        if (!enabled)
        {
            return "Disabled";
        }

        var hero = village.Status?.HeroStatus;
        if (hero is null)
        {
            return "Not loaded";
        }

        if (hero.IsDead)
        {
            var revive = hero.ReviveFinish?.RemainingSecondsAt(nowUtc) ?? hero.ReviveRemainingSeconds;
            return revive is > 0 ? $"Reviving: {FormatCountdown(TimeSpan.FromSeconds(revive.Value))}" : "Dead";
        }

        var returning = hero.ReturnFinish?.RemainingSecondsAt(nowUtc) ?? hero.SecondsUntilReturn;
        if (returning is > 0)
        {
            return $"Returning: {FormatCountdown(TimeSpan.FromSeconds(returning.Value))}";
        }

        var adventure = hero.AdventureReadyFinish?.RemainingSecondsAt(nowUtc) ?? hero.SecondsUntilAdventureReady;
        return adventure is > 0
            ? $"Adventure: {FormatCountdown(TimeSpan.FromSeconds(adventure.Value))}"
            : "Ready";
    }

    private static string ResolveTownHall(
        VillageOverviewSource village,
        IReadOnlyList<PipelineTaskSource> tasks,
        bool enabled,
        DateTimeOffset nowUtc)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        if (village.TownHallEndsAtUtc is DateTimeOffset endsAt && endsAt > nowUtc)
        {
            var mode = string.IsNullOrWhiteSpace(village.TownHallMode) ? "Celebration" : village.TownHallMode;
            return $"{mode}: {FormatCountdown(endsAt - nowUtc)}";
        }

        return ResolveQueueGroupStatus(tasks, QueueGroup.TownHallCelebration, enabled, nowUtc, "Ready");
    }

    private static string ResolveBrewery(
        VillageStatus? status,
        IReadOnlyList<PipelineTaskSource> tasks,
        bool enabled,
        DateTimeOffset nowUtc)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        var brewery = status?.BreweryCelebrationStatus;
        if (brewery is null)
        {
            return HasQueueGroupWork(tasks, QueueGroup.BreweryCelebration)
                ? ResolveQueueGroupStatus(tasks, QueueGroup.BreweryCelebration, enabled, nowUtc, "Ready")
                : "Not loaded";
        }

        if (!brewery.IsAvailableForTribe || brewery.IsCapital == false || !brewery.BreweryExists)
        {
            return "Not available";
        }

        var remaining = brewery.Finish?.RemainingSecondsAt(nowUtc) ?? brewery.RemainingSeconds;
        if (brewery.CelebrationRunning && remaining is > 0)
        {
            return $"Running: {FormatCountdown(TimeSpan.FromSeconds(remaining.Value))}";
        }

        return HasQueueGroupWork(tasks, QueueGroup.BreweryCelebration)
            ? ResolveQueueGroupStatus(tasks, QueueGroup.BreweryCelebration, enabled, nowUtc, "Ready")
            : "Ready";
    }

    private static string ResolveQueueGroupStatus(
        IReadOnlyList<PipelineTaskSource> tasks,
        QueueGroup group,
        bool enabled,
        DateTimeOffset nowUtc,
        string emptyText)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        var groupTasks = tasks.Where(source => source.Item.Group == group).ToList();
        var running = groupTasks.FirstOrDefault(source => source.Item.Status == QueueStatus.Running);
        if (running is not null)
        {
            return $"Running: {running.DisplayName}";
        }

        var deferred = groupTasks
            .Where(source => source.Item.Status == QueueStatus.Pending && source.Item.NextAttemptAt > nowUtc)
            .OrderBy(source => source.Item.NextAttemptAt)
            .FirstOrDefault();
        if (deferred is not null)
        {
            return $"{deferred.DisplayName}: {FormatCountdown(deferred.Item.NextAttemptAt - nowUtc)}";
        }

        var ready = groupTasks.FirstOrDefault(source =>
            source.Item.Status == QueueStatus.Pending && source.Item.NextAttemptAt <= nowUtc);
        return ready is null ? emptyText : $"Ready: {ready.DisplayName}";
    }

    private static bool IsGroupEnabled(VillageOverviewSource village, QueueGroup group)
        => village.IsEnabled
            && village.EnabledGroups.Contains(QueueGroupCatalog.GetKey(group));

    private static bool HasQueueGroupWork(IReadOnlyList<PipelineTaskSource> tasks, QueueGroup group)
        => tasks.Any(source => source.Item.Group == group
            && source.Item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);

    private static string FormatQueueRow(string name, string level, string countdown)
    {
        if (string.Equals(name, "Ready", StringComparison.Ordinal)
            || string.Equals(name, "Not loaded", StringComparison.Ordinal))
        {
            return name;
        }

        // Compact form: "Palisade  Lvl 5  05:01" — abbreviate "Level", and separate with spaces (no dots).
        var shortLevel = level.StartsWith("Level ", StringComparison.Ordinal)
            ? "Lvl " + level["Level ".Length..]
            : level;
        return $"{name}  {shortLevel}  {countdown}";
    }

    private static string DescribeTask(PipelineTaskSource source)
        => string.IsNullOrWhiteSpace(source.VillageName)
            ? $"{source.DisplayName} (Account-wide)"
            : $"{source.DisplayName} ({source.VillageName})";

    private static string FormatCountdown(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }
}
