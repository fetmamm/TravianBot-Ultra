using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionInitialFillTests
{
    [Fact]
    public void FirstEmptyObservation_StaysActiveUntilCategoryIsFull()
    {
        var session = new TravianSessionCache();
        const string category = "xy:112|7:shared";

        Assert.True(session.ObserveConstructionInitialFill(category, ongoingCount: 0, canStart: true));

        session.ConstructionOngoingByKey[category] = 0;
        Assert.True(session.ObserveConstructionInitialFill(category, ongoingCount: 1, canStart: true));
        Assert.True(session.IsConstructionInitialFillActive(category));

        Assert.False(session.ObserveConstructionInitialFill(category, ongoingCount: 2, canStart: false));
        Assert.False(session.IsConstructionInitialFillActive(category));
    }

    [Fact]
    public void SlotFreedAfterKnownConstruction_DoesNotStartInitialFill()
    {
        var session = new TravianSessionCache();
        const string category = "xy:114|4:shared";
        session.ConstructionOngoingByKey[category] = 1;

        Assert.False(session.ObserveConstructionInitialFill(category, ongoingCount: 0, canStart: true));
        Assert.False(session.IsConstructionInitialFillActive(category));
    }
}
