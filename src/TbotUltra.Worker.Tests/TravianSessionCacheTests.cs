using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianSessionCacheTests
{
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
