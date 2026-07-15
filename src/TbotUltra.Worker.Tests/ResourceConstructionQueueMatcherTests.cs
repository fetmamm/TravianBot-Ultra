using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ResourceConstructionQueueMatcherTests
{
    [Fact]
    public void HighestQueuedLevelForSlot_DoesNotMatchSiblingResourceSlots()
    {
        var active = new[]
        {
            new ActiveConstruction(ConstructionKind.Resource, "Cropland", 5, 440, null, SlotId: 9),
        };

        var level = ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(active, 13, "Cropland", 4);

        Assert.Equal(4, level);
    }

    [Fact]
    public void HighestQueuedLevelForSlot_MatchesExactResourceSlot()
    {
        var active = new[]
        {
            new ActiveConstruction(ConstructionKind.Resource, "Cropland", 5, 440, null, SlotId: 13),
        };

        var level = ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(active, 13, "Cropland", 4);

        Assert.Equal(5, level);
    }

    [Fact]
    public void HighestQueuedLevelForSlot_FallsBackToNameWhenQueuedSlotIsUnknown()
    {
        var active = new[]
        {
            new ActiveConstruction(ConstructionKind.Resource, "Cropland", 5, 440, null),
        };

        var level = ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(active, 13, "Cropland", 4);

        Assert.Equal(5, level);
    }

    [Fact]
    public void HighestQueuedLevelForSlot_UsesCompactBuildQueueFallback()
    {
        var queue = new[]
        {
            new BuildQueueItem("Iron mine level 8", "00:04:21", SlotId: 4),
        };

        var level = ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(queue, 4, "Iron mine", 7);

        Assert.Equal(8, level);
    }

    [Fact]
    public void HighestQueuedLevelForSlot_DoesNotUseUnrelatedQueueChange()
    {
        var queue = new[]
        {
            new BuildQueueItem("Clay pit level 8", "00:04:21", SlotId: 5),
        };

        var level = ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot(queue, 4, "Iron mine", 7);

        Assert.Equal(7, level);
    }
}
