using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SmithyQueueStateTests
{
    [Fact]
    public void PreserveKnownActiveQueue_EmptyReadDoesNotEraseFutureQueue()
    {
        var now = DateTimeOffset.UtcNow;
        var finish = new TimerSnapshot(600, now, now.AddMinutes(10), false);
        var existing = CreateStatus(
            new ActiveSmithyUpgrade("Phalanx", 4, 600, finish));
        var incoming = CreateStatus();

        var result = SmithyQueueState.PreserveKnownActiveQueue(incoming, existing, now);

        var active = Assert.Single(SmithyQueueState.ResolveActiveUpgrades(result, now));
        Assert.Equal("Phalanx", active.Name);
        Assert.Equal(4, active.TargetLevel);
    }

    [Fact]
    public void PreserveKnownActiveQueue_NewBrowserQueueReplacesOldQueue()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = CreateStatus(
            new ActiveSmithyUpgrade("Phalanx", 4, 600, new TimerSnapshot(600, now, now.AddMinutes(10), false)));
        var incoming = CreateStatus(
            new ActiveSmithyUpgrade("Pathfinder", 2, 300, new TimerSnapshot(300, now, now.AddMinutes(5), false)));

        var result = SmithyQueueState.PreserveKnownActiveQueue(incoming, existing, now);

        Assert.Equal("Pathfinder", Assert.Single(result.ActiveUpgrades!).Name);
    }

    [Fact]
    public void PreserveKnownActiveQueue_ExpiredQueueCanBeCleared()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = CreateStatus(
            new ActiveSmithyUpgrade("Phalanx", 4, 0, new TimerSnapshot(600, now.AddMinutes(-11), now.AddMinutes(-1), false)));
        var incoming = CreateStatus();

        var result = SmithyQueueState.PreserveKnownActiveQueue(incoming, existing, now);

        Assert.Empty(SmithyQueueState.ResolveActiveUpgrades(result, now));
    }

    private static SmithyUpgradeStatus CreateStatus(params ActiveSmithyUpgrade[] upgrades)
    {
        var remaining = upgrades
            .Where(entry => entry.TimeLeftSeconds is > 0)
            .Select(entry => entry.TimeLeftSeconds!.Value)
            .OrderBy(value => value)
            .ToList();
        return new SmithyUpgradeStatus(
            SmithyExists: true,
            SmithySlotId: 21,
            ActiveUpgradeCount: upgrades.Length,
            RemainingSeconds: remaining.FirstOrDefault() is var first && first > 0 ? first : null,
            ActiveUpgradeRemainingSeconds: remaining,
            RemainingText: remaining.Count > 0 ? "active" : "Ready",
            StatusText: remaining.Count > 0 ? "active" : "Ready",
            ActiveUpgradeFinishes: upgrades.Where(entry => entry.Finish is not null).Select(entry => entry.Finish!).ToList(),
            ActiveUpgrades: upgrades);
    }
}
