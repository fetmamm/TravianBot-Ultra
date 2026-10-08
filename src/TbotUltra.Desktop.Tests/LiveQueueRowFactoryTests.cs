using System;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LiveQueueRowFactoryTests
{
    [Fact]
    public void BuildConstructionRows_PadsRomansToThreeSlotsAndCountsDown()
    {
        var now = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var finish = TimerSnapshot.FromRemaining(90, now);
        var active = new[]
        {
            new ActiveConstruction(
                ConstructionKind.Building,
                "Warehouse",
                5,
                90,
                null,
                finish),
        };

        var rows = LiveQueueRowFactory.BuildConstructionRows(
            active,
            slotCount: 3,
            hasStatus: true,
            now.AddSeconds(30),
            value => value.ToString("HH:mm:ss"));

        Assert.Equal(3, rows.Count);
        Assert.Equal("01:00", rows[0].CountdownText);
        Assert.Equal("Ready", rows[1].Name);
        Assert.Equal("Ready", rows[2].Name);
    }

    [Fact]
    public void BuildConstructionRows_ShowsNotLoadedWhenCachedConstructionExpired()
    {
        var now = new DateTimeOffset(2026, 6, 16, 20, 0, 0, TimeSpan.Zero);
        var status = new VillageStatus(
            ActiveVillage: "A",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: [],
            BuildQueue: []) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Warehouse",
                    19,
                    null,
                    null,
                    TimerSnapshot.FromRemaining(60, now.AddMinutes(-5))),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var snapshot = ConstructionQueueState.ResolveSnapshot(status, now);

        var rows = LiveQueueRowFactory.BuildConstructionRows(
            ConstructionQueueState.ResolveCurrentActiveConstructions(status, now),
            slotCount: 2,
            hasStatus: snapshot.Knowledge != ConstructionQueueKnowledge.Unknown,
            now,
            value => value.ToString("HH:mm:ss"));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Not loaded", row.Name));
    }

    [Fact]
    public void BuildConstructionRows_AppendsActiveWatchtowersAfterOrdinarySlots()
    {
        var observed = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var status = new WatchtowerStatus(
            17,
            [
                new WatchtowerConstruction(18, 120, null, TimerSnapshot.FromRemaining(120, observed)),
                new WatchtowerConstruction(19, 240, null, TimerSnapshot.FromRemaining(240, observed)),
            ],
            observed);

        var rows = LiveQueueRowFactory.BuildConstructionRows(
            [],
            slotCount: 2,
            hasStatus: true,
            observed.AddSeconds(30),
            value => value.ToString("HH:mm:ss"),
            status);

        Assert.Equal(4, rows.Count);
        Assert.All(rows.Take(2), row => Assert.Equal("Ready", row.Name));
        Assert.All(rows.Skip(2), row => Assert.True(row.IsWatchtower));
        Assert.Equal("Watchtowers", rows[2].Name);
        Assert.Equal("Level 18", rows[2].LevelText);
        Assert.Equal("01:30", rows[2].CountdownText);
        Assert.Equal("10:02:00", rows[2].FinishAtText);
        Assert.Equal("Level 19", rows[3].LevelText);
    }

    [Fact]
    public void BuildConstructionRows_HidesExpiredWatchtowerTimer()
    {
        var observed = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var status = new WatchtowerStatus(
            17,
            [new WatchtowerConstruction(18, 30, null, TimerSnapshot.FromRemaining(30, observed))],
            observed);

        var rows = LiveQueueRowFactory.BuildConstructionRows(
            [],
            slotCount: 2,
            hasStatus: true,
            observed.AddMinutes(1),
            value => value.ToString("HH:mm:ss"),
            status);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsWatchtower));
    }

    [Fact]
    public void BuildQueueRow_ReconcilesWatchtowerHighlightWhenQueueChanges()
    {
        var row = new TravianBuildQueueRow { Name = "Ready" };
        var changed = new List<string>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        row.ApplySnapshot(new TravianBuildQueueRow { Name = "Watchtowers", IsWatchtower = true });

        Assert.True(row.IsWatchtower);
        Assert.Contains(nameof(TravianBuildQueueRow.IsWatchtower), changed);
    }

    [Fact]
    public void BuildSmithyRows_AlwaysReturnsTwoRows()
    {
        var rows = LiveQueueRowFactory.BuildSmithyRows(
            [],
            slotCount: 2,
            hasStatus: false,
            DateTimeOffset.UtcNow,
            value => value.ToString("HH:mm:ss"));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Not loaded", row.Name));
    }
}
