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

    [Fact]
    public void BuildConsideredGroups_AppendsActiveManualGroupsOnce()
    {
        var manualConstruction = Item("upgrade_building_to_level", QueueStatus.Pending, 0);
        manualConstruction.Group = QueueGroup.Construction;
        var duplicateHero = Item("hero_manage", QueueStatus.Running, 0);
        duplicateHero.Group = QueueGroup.Hero;
        var completedManual = Item("send_farmlists", QueueStatus.Succeeded, 0);
        completedManual.Group = QueueGroup.Farming;
        var runtimeSmithy = Item("upgrade_smithy", QueueStatus.Pending, 0);
        runtimeSmithy.Group = QueueGroup.Troops;
        runtimeSmithy.IsRuntimeOnly = true;

        var groups = ContinuousLoopSelector.BuildConsideredGroups(
            [QueueGroup.Hero],
            [manualConstruction, duplicateHero, completedManual, runtimeSmithy]);

        Assert.Equal([QueueGroup.Hero, QueueGroup.Construction], groups);
    }

    [Fact]
    public void SelectReadyUtilityItem_PrefersActiveVillageWithoutMutatingOrder()
    {
        var otherVillage = Item("collect_tasks", QueueStatus.Pending, 0);
        otherVillage.Payload["village"] = "b";
        var activeVillage = Item("collect_daily_quests", QueueStatus.Pending, 0);
        activeVillage.Payload["village"] = "a";
        var items = new[] { otherVillage, activeVillage };

        var result = ContinuousLoopSelector.SelectReadyUtilityItem(
            items,
            "a",
            item => item.Payload["village"]);

        Assert.Same(activeVillage, result);
        Assert.Equal(new[] { otherVillage, activeVillage }, items);
    }

    [Theory]
    [InlineData("collect_tasks", true)]
    [InlineData("COLLECT_DAILY_QUESTS", true)]
    [InlineData("hero_manage", false)]
    public void IsUtilityTask_UsesExistingTaskSet(string taskName, bool expected)
    {
        Assert.Equal(expected, ContinuousLoopSelector.IsUtilityTask(taskName));
    }

    [Fact]
    public void PayloadEquals_RequiresSameKeysAndOrdinalValues()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = "Timed",
        };

        Assert.True(ContinuousLoopSelector.PayloadEquals(
            current,
            new Dictionary<string, string> { ["MODE"] = "Timed" }));
        Assert.False(ContinuousLoopSelector.PayloadEquals(
            current,
            new Dictionary<string, string> { ["mode"] = "timed" }));
        Assert.False(ContinuousLoopSelector.PayloadEquals(
            current,
            new Dictionary<string, string> { ["mode"] = "Timed", ["extra"] = "1" }));
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
