using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianSessionCacheTests
{
    [Fact]
    public void RecentVillageStatus_IsOneShotAndRequiresMatchingStableVillageKey()
    {
        var now = new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
        var status = new TbotUltra.Worker.Domain.VillageStatus(
            "HJO", [], new Dictionary<string, string>(), [], [], [],
            ActiveVillageCoordX: 164, ActiveVillageCoordY: 109);
        var cache = new TravianSessionCache();
        cache.SaveRecentVillageStatus(status, now);

        Assert.Null(cache.TryTakeRecentVillageStatus("xy:164|110", now, TimeSpan.FromSeconds(20)));

        cache.SaveRecentVillageStatus(status, now);
        Assert.Same(status, cache.TryTakeRecentVillageStatus("xy:164|109", now.AddSeconds(10), TimeSpan.FromSeconds(20)));
        Assert.Null(cache.TryTakeRecentVillageStatus("xy:164|109", now.AddSeconds(11), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void RecentVillageStatus_ExpiresBeforeItCanDriveAnotherTask()
    {
        var now = new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
        var status = new TbotUltra.Worker.Domain.VillageStatus(
            "HJO", [], new Dictionary<string, string>(), [], [], [],
            ActiveVillageCoordX: 164, ActiveVillageCoordY: 109);
        var cache = new TravianSessionCache();
        cache.SaveRecentVillageStatus(status, now);

        Assert.Null(cache.TryTakeRecentVillageStatus("xy:164|109", now.AddSeconds(21), TimeSpan.FromSeconds(20)));
    }
    [Fact]
    public void SynchronizeConstructionHumanizeState_ClearsOnlyOnVersionChange()
    {
        var cache = new TravianSessionCache();
        Assert.True(cache.SynchronizeConstructionHumanizeState(1));

        cache.ConstructionOngoingByKey["village:building"] = 1;
        cache.ConstructionHumanizeUntilBySlot["village:building:20"] = DateTimeOffset.UtcNow.AddMinutes(2);

        Assert.False(cache.SynchronizeConstructionHumanizeState(1));
        Assert.NotEmpty(cache.ConstructionOngoingByKey);
        Assert.NotEmpty(cache.ConstructionHumanizeUntilBySlot);

        Assert.True(cache.SynchronizeConstructionHumanizeState(2));
        Assert.Empty(cache.ConstructionOngoingByKey);
        Assert.Empty(cache.ConstructionHumanizeUntilBySlot);
    }
}
