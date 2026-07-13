using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousLoopSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectReadyGroupHead_DoesNotSkipDeferredHead()
    {
        var deferredHead = Item("first", QueueStatus.Pending, 60);
        var readySecond = Item("second", QueueStatus.Pending, 0);

        var result = ContinuousLoopSelector.SelectReadyGroupHead([deferredHead, readySecond], Now);

        Assert.Null(result);
    }

    [Fact]
    public void SelectReadyHeroGroupItem_AllowsAttributeTaskBehindDeferredHeroHead()
    {
        var deferredHead = Item("hero_manage", QueueStatus.Pending, 60);
        var attributeTask = Item("spend_hero_attribute_points", QueueStatus.Pending, 0);

        var result = ContinuousLoopSelector.SelectReadyHeroGroupItem([deferredHead, attributeTask], Now);

        Assert.Same(attributeTask, result);
    }

    [Fact]
    public void SelectReadyHeroGroupItem_PreservesReadyHeadPriority()
    {
        var readyHead = Item("hero_manage", QueueStatus.Pending, 0);
        var attributeTask = Item("spend_hero_attribute_points", QueueStatus.Pending, 0);

        var result = ContinuousLoopSelector.SelectReadyHeroGroupItem([readyHead, attributeTask], Now);

        Assert.Same(readyHead, result);
    }

    private static QueueItem Item(string taskName, QueueStatus status, int secondsUntilReady)
        => new()
        {
            Id = Guid.NewGuid(),
            TaskName = taskName,
            Status = status,
            NextAttemptAt = Now.AddSeconds(secondsUntilReady),
        };
}
