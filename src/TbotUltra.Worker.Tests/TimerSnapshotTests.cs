using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TimerSnapshotTests
{
    [Fact]
    public void FromRemaining_UsesProgramClockWhenServerTimeIsMissing()
    {
        var before = DateTimeOffset.UtcNow;
        var snapshot = TimerSnapshot.FromRemaining(90);
        var after = DateTimeOffset.UtcNow;

        Assert.False(snapshot.FromServerTime);
        Assert.InRange(snapshot.ReadAtUtc, before, after);
        Assert.Equal(snapshot.ReadAtUtc.AddSeconds(90), snapshot.FinishUtc);
    }

    [Fact]
    public void FromRemaining_UsesSuppliedAbsoluteClock()
    {
        var readAt = new DateTimeOffset(2026, 6, 14, 10, 0, 0, TimeSpan.FromHours(2));

        var snapshot = TimerSnapshot.FromRemaining(30, readAt);

        Assert.True(snapshot.FromServerTime);
        Assert.Equal(readAt.ToUniversalTime(), snapshot.ReadAtUtc);
        Assert.Equal(readAt.ToUniversalTime().AddSeconds(30), snapshot.FinishUtc);
    }

    [Fact]
    public void RemainingSecondsAt_RoundsUpAndClampsToZero()
    {
        var readAt = new DateTimeOffset(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);
        var snapshot = TimerSnapshot.FromRemaining(10, readAt);

        Assert.Equal(6, snapshot.RemainingSecondsAt(readAt.AddSeconds(4.1)));
        Assert.Equal(0, snapshot.RemainingSecondsAt(readAt.AddSeconds(11)));
    }

    [Fact]
    public void IsFinishedAt_IncludesFinishBoundary()
    {
        var readAt = new DateTimeOffset(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);
        var snapshot = TimerSnapshot.FromRemaining(10, readAt);

        Assert.False(snapshot.IsFinishedAt(snapshot.FinishUtc.AddTicks(-1)));
        Assert.True(snapshot.IsFinishedAt(snapshot.FinishUtc));
    }
}
