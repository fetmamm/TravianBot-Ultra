using TbotUltra.Worker.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianSessionCacheTests
{
    [Theory]
    [InlineData(CityCapability.Unknown)]
    [InlineData(CityCapability.Disabled)]
    public void ConfirmCitiesCapabilityFromCity_UsesConfirmedCityAsPositiveEvidence(CityCapability initial)
    {
        var cache = new TravianSessionCache { CityCapability = initial };

        Assert.True(cache.ConfirmCitiesCapabilityFromCity());
        Assert.Equal(CityCapability.Enabled, cache.CityCapability);
        Assert.False(cache.ConfirmCitiesCapabilityFromCity());
    }

    [Fact]
    public void RestoreWatchtowerSnapshot_RecoversCityKnowledgeForQuickRelogin()
    {
        var cache = new TravianSessionCache
        {
            CachedVillages = [new Village("WHY", "dorf1.php?newdid=1", false, CoordX: 164, CoordY: 110)],
        };
        var status = new WatchtowerStatus(19, [], DateTimeOffset.UtcNow);

        cache.RestoreWatchtowerSnapshot("xy:164|110", status);

        Assert.Equal(CityCapability.Enabled, cache.CityCapability);
        Assert.Equal(CityStatus.City, cache.VillageCityStatuses["xy:164|110"]);
        Assert.Equal(CityStatus.City, Assert.Single(cache.CachedVillages!).CityStatus);
        Assert.Same(status, cache.WatchtowerStatuses["xy:164|110"]);

        var sidebarVillage = new Village("WHY", "dorf1.php?newdid=1", false, CoordX: 164, CoordY: 110);
        var merged = VillageIdentityReconciler.MergeFreshWithCached(sidebarVillage, cache.CachedVillages);
        Assert.Equal(CityStatus.City, merged.CityStatus);
    }

    [Fact]
    public void RestoreWatchtowerSnapshot_UpdatesOlderProfileVillageLabel()
    {
        var cache = new TravianSessionCache
        {
            CachedVillages = [new Village("WHY", "dorf1.php?newdid=1", false, CoordX: 164, CoordY: 110,
                CityStatus: CityStatus.Village)],
        };

        cache.RestoreWatchtowerSnapshot(
            "xy:164|110",
            new WatchtowerStatus(19, [], DateTimeOffset.UtcNow));

        Assert.Equal(CityStatus.City, Assert.Single(cache.CachedVillages!).CityStatus);
    }

    [Fact]
    public void ObserveCityCapability_UnknownLoginProbeDoesNotEraseConfirmedCity()
    {
        var cache = new TravianSessionCache();
        cache.ConfirmCitiesCapabilityFromCity();

        Assert.Equal(CityCapability.Enabled, cache.ObserveCityCapability(CityCapability.Unknown));
        Assert.Equal(CityCapability.Enabled, cache.CityCapability);
        Assert.Equal(CityCapability.Disabled, cache.ObserveCityCapability(CityCapability.Disabled));
    }

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
