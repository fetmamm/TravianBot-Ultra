using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TroopTrainingQueueStateTests
{
    [Fact]
    public void PreserveKnownActiveQueue_EmptyReadKeepsStillTickingQueue()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = new[] { Barracks(600, new TimerSnapshot(600, now, now.AddMinutes(10), true)) };
        var incoming = new[] { BarracksEmpty() };

        var result = TroopTrainingQueueState.PreserveKnownActiveQueue(incoming, existing, now);

        var row = Assert.Single(result);
        Assert.NotNull(row.Finish);
        Assert.True(row.RemainingSeconds is > 0);
    }

    [Fact]
    public void PreserveKnownActiveQueue_LiveReadWins()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = new[] { Barracks(600, new TimerSnapshot(600, now, now.AddMinutes(10), true)) };
        var incoming = new[] { Barracks(120, new TimerSnapshot(120, now, now.AddMinutes(2), true)) };

        var result = TroopTrainingQueueState.PreserveKnownActiveQueue(incoming, existing, now);

        Assert.Equal(120, Assert.Single(result).RemainingSeconds);
    }

    [Fact]
    public void PreserveKnownActiveQueue_ExpiredCachedQueueDropped()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = new[] { Barracks(0, new TimerSnapshot(600, now.AddMinutes(-11), now.AddMinutes(-1), true)) };
        var incoming = new[] { BarracksEmpty() };

        var result = TroopTrainingQueueState.PreserveKnownActiveQueue(incoming, existing, now);

        var row = Assert.Single(result);
        Assert.Null(row.Finish);
        Assert.Null(row.RemainingSeconds);
    }

    [Theory]
    [InlineData(7200, "1", true)]
    [InlineData(3600, "1", false)]
    [InlineData(7200, "no_limit", false)]
    [InlineData(7200, null, false)]
    public void IsOverMaxQueue_OnlyTrueWhenRemainingExceedsFiniteLimit(
        int remainingSeconds,
        string? maxQueueMode,
        bool expected)
    {
        Assert.Equal(expected, TroopTrainingQueueState.IsOverMaxQueue(remainingSeconds, maxQueueMode));
    }

    private static TroopTrainingQueueStatus Barracks(int remaining, TimerSnapshot finish) =>
        new(
            TroopTrainingBuildingType.Barracks,
            "Barracks",
            Exists: true,
            SlotId: 19,
            QueueItems: [],
            RemainingSeconds: remaining > 0 ? remaining : null,
            RemainingText: remaining > 0 ? "active" : "Ready",
            Finish: finish);

    private static TroopTrainingQueueStatus BarracksEmpty() =>
        new(
            TroopTrainingBuildingType.Barracks,
            "Barracks",
            Exists: true,
            SlotId: 19,
            QueueItems: [],
            RemainingSeconds: null,
            RemainingText: "Ready",
            Finish: null);
}
