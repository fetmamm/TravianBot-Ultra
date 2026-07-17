using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageOverviewFactoryTests
{
    private static readonly IReadOnlySet<string> AllGroups = QueueGroupCatalog.AllGroups
        .Select(QueueGroupCatalog.GetKey)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Create_SeparatesRunningAndProjectsFiveWithoutMutatingQueue()
    {
        var now = new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
        var running = Task("running", QueueGroup.Hero, "A", now, QueueStatus.Running, priority: 100);
        var pending = Enumerable.Range(1, 6)
            .Select(index => Task($"task {index}", QueueGroup.Construction, "A", now, priority: 10 - index))
            .ToList();
        var originalAttempts = pending.ToDictionary(source => source.Item.Id, source => source.Item.NextAttemptAt);

        var snapshot = VillageOverviewFactory.Create(
            [Village("A", "A")],
            [running, .. pending],
            [QueueGroup.Construction, QueueGroup.Hero],
            "A",
            pending[0].Item,
            now,
            value => value.ToString("HH:mm:ss"));

        Assert.Contains("running", snapshot.RunningTask);
        Assert.Equal(5, snapshot.UpcomingTasks.Count);
        Assert.Equal("Exact next", snapshot.UpcomingTasks[0].Source);
        Assert.Equal("Now", snapshot.UpcomingTasks[0].Timing);
        Assert.All(snapshot.UpcomingTasks.Skip(1), row => Assert.Equal("Projection", row.Source));
        Assert.All(snapshot.UpcomingTasks.Skip(1), row => Assert.Contains("after previous", row.Timing));
        Assert.All(pending, source => Assert.Equal(originalAttempts[source.Item.Id], source.Item.NextAttemptAt));
        Assert.All(pending, source => Assert.Equal(QueueStatus.Pending, source.Item.Status));
    }

    [Fact]
    public void Create_UsesEarliestForDeferredAndShowsPausedHeadAsBlocked()
    {
        var now = new DateTimeOffset(2026, 7, 17, 11, 0, 0, TimeSpan.Zero);
        var paused = Task("paused construction", QueueGroup.Construction, "A", now, QueueStatus.Paused);
        var deferred = Task("farm later", QueueGroup.Farming, "A", now.AddMinutes(5));

        var snapshot = VillageOverviewFactory.Create(
            [Village("A", "A")],
            [paused, deferred],
            [QueueGroup.Construction, QueueGroup.Farming],
            null,
            null,
            now,
            value => value.ToString("HH:mm:ss"));

        Assert.Equal("farm later", snapshot.UpcomingTasks[0].Task);
        Assert.Equal("Earliest in 05:00", snapshot.UpcomingTasks[0].Timing);
        Assert.Equal("paused construction", snapshot.UpcomingTasks[1].Task);
        Assert.Equal("Blocked (paused queue head)", snapshot.UpcomingTasks[1].Timing);
    }

    [Fact]
    public void Create_UsesActiveTimersForConstructionSmithyTroopsFarmingAndHero()
    {
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var status = new VillageStatus(
            "A",
            [],
            new Dictionary<string, string>(),
            [],
            [],
            [],
            Tribe: "Romans") with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Warehouse",
                    5,
                    120,
                    null,
                    TimerSnapshot.FromRemaining(120, now)),
            ],
            ActiveConstructionsFromOverview = true,
            SmithyUpgradeStatus = new SmithyUpgradeStatus(
                true,
                20,
                1,
                180,
                [180],
                "03:00",
                "Running",
                [TimerSnapshot.FromRemaining(180, now)],
                [new ActiveSmithyUpgrade("Imperian", 4, 180, TimerSnapshot.FromRemaining(180, now))]),
            TroopTrainingQueues =
            [
                new TroopTrainingQueueStatus(
                    TroopTrainingBuildingType.Barracks,
                    "Barracks",
                    true,
                    19,
                    [],
                    240,
                    "04:00",
                    TimerSnapshot.FromRemaining(240, now)),
            ],
            FarmLists =
            [
                new FarmListOverview("Near", 10, 10, 300, Finish: TimerSnapshot.FromRemaining(300, now)),
            ],
            HeroStatus = new HeroStatus(
                Exists: true,
                State: "Returning",
                SecondsUntilReturn: 360,
                ReturnFinish: TimerSnapshot.FromRemaining(360, now)),
        };

        var snapshot = VillageOverviewFactory.Create(
            [Village("A", "A", status, isHeroHome: true, tribe: "Romans")],
            [],
            QueueGroupCatalog.AllGroups,
            "A",
            null,
            now.AddSeconds(30),
            value => value.ToString("HH:mm:ss"));
        var row = Assert.Single(snapshot.Villages);

        Assert.Contains("Warehouse  Lvl 5  01:30", row.Construction);
        Assert.DoesNotContain("12:02:00", row.Construction);
        Assert.DoesNotContain("Level", row.Construction);
        Assert.Equal(3, row.Construction.Split('\n').Length);
        Assert.Contains("Imperian  Lvl 4  02:30", row.Smithy);
        Assert.DoesNotContain("12:03:00", row.Smithy);
        Assert.Contains("Barracks: 03:30", row.BuildTroops);
        Assert.Contains("Near: 04:30", row.Farming);
        Assert.Equal("Returning: 05:30", row.Hero);
    }

    [Fact]
    public void Create_DisabledVillageDoesNotAppearReady()
    {
        var now = DateTimeOffset.UtcNow;
        var village = Village("A", "A") with { IsEnabled = false };

        var row = Assert.Single(VillageOverviewFactory.Create(
            [village],
            [],
            QueueGroupCatalog.AllGroups,
            null,
            null,
            now,
            value => value.ToString("HH:mm:ss")).Villages);

        Assert.Equal("Disabled", row.NextTask);
        Assert.Equal("Disabled", row.Construction);
        Assert.Equal("Disabled", row.Smithy);
        Assert.Equal("Disabled", row.Farming);
    }

    [Fact]
    public void Create_LabelsVillageLessTaskAsAccountWide()
    {
        var now = DateTimeOffset.UtcNow;
        var task = Task("collect rewards", QueueGroup.Hero, null, now);

        var row = Assert.Single(VillageOverviewFactory.Create(
            [Village("A", "A")],
            [task],
            [QueueGroup.Hero],
            null,
            task.Item,
            now,
            value => value.ToString("HH:mm:ss")).UpcomingTasks);

        Assert.Equal("Account-wide", row.Village);
    }

    [Fact]
    public void Create_ProjectionKeepsGroupVillageRotation()
    {
        var now = DateTimeOffset.UtcNow;
        var villageA = Task("A task", QueueGroup.Farming, "A", now, priority: 10);
        var villageB = Task("B task", QueueGroup.Farming, "B", now, priority: 1);

        var first = VillageOverviewFactory.Create(
            [Village("A", "A"), Village("B", "B")],
            [villageA, villageB],
            [QueueGroup.Farming],
            null,
            null,
            now,
            value => value.ToString("HH:mm:ss"),
            new Dictionary<QueueGroup, string?> { [QueueGroup.Farming] = "B" }).UpcomingTasks.First();

        Assert.Equal("B task", first.Task);
    }

    [Fact]
    public void Create_UsesStableKeysForDuplicateNamesAndPrefersDeferredAndTownHallTimers()
    {
        var now = new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero);
        var villageA = Village("key-a", "Same");
        var villageB = Village("key-b", "Same") with
        {
            TownHallMode = "Small",
            TownHallEndsAtUtc = now.AddMinutes(10),
        };
        var deferred = Task("Warehouse level 6", QueueGroup.Construction, "key-b", now.AddMinutes(2));

        var rows = VillageOverviewFactory.Create(
            [villageA, villageB],
            [deferred],
            [QueueGroup.Construction],
            null,
            null,
            now,
            value => value.ToString("HH:mm:ss")).Villages;

        Assert.Equal("Nothing queued", rows[0].NextTask);
        Assert.StartsWith("Waiting 02:00", rows[1].NextTask);
        Assert.Contains("Warehouse level 6: 02:00", rows[1].Construction);
        Assert.Equal("Small: 10:00", rows[1].TownHall);
    }

    [Fact]
    public void Create_AttributesTaskByNameWhenKeyMatchesNoVillage()
    {
        var now = new DateTimeOffset(2026, 7, 17, 16, 0, 0, TimeSpan.Zero);
        // The dashboard row resolved only to a name key (store had no coordinates for it), while the queued
        // construction task carries the village's coordinate key. The keys differ for the same village, so a
        // key-only join would wrongly read "Nothing queued" even though the task targets this village.
        var village = Village("name:02 kong", "02 KONG");
        var task = Task("Upgrade Stable to level 10", QueueGroup.Construction, "xy:29|-4", now)
            with { VillageName = "02 KONG" };

        var row = Assert.Single(VillageOverviewFactory.Create(
            [village],
            [task],
            [QueueGroup.Construction],
            null,
            null,
            now,
            value => value.ToString("HH:mm:ss")).Villages);

        Assert.StartsWith("Ready:", row.NextTask);
        Assert.Contains("Upgrade Stable to level 10", row.NextTask);
    }

    private static PipelineTaskSource Task(
        string name,
        QueueGroup group,
        string? villageKey,
        DateTimeOffset nextAttemptAt,
        QueueStatus status = QueueStatus.Pending,
        int priority = 0)
    {
        return new PipelineTaskSource(
            new QueueItem
            {
                TaskName = name,
                Group = group,
                Status = status,
                Priority = priority,
                NextAttemptAt = nextAttemptAt,
                CreatedAt = nextAttemptAt.AddHours(-1),
            },
            name,
            villageKey,
            villageKey ?? string.Empty,
            true);
    }

    private static VillageOverviewSource Village(
        string key,
        string name,
        VillageStatus? status = null,
        bool isHeroHome = false,
        string tribe = "Gauls")
    {
        return new VillageOverviewSource(
            key,
            name,
            "100",
            tribe,
            true,
            AllGroups,
            isHeroHome,
            null,
            null,
            status);
    }
}
