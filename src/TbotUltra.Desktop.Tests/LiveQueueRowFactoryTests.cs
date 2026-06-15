using System;
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
