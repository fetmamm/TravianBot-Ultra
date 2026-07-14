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
    public void SelectReadyGroupHead_DoesNotRunPausedHead()
    {
        var pausedHead = Item("first", QueueStatus.Paused, 0);
        var readySecond = Item("second", QueueStatus.Pending, 0);

        var result = ContinuousLoopSelector.SelectReadyGroupHead([pausedHead, readySecond], Now);

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

    [Fact]
    public void CreatePlan_FiltersSettingsAndKeepsSchedulerOrder()
    {
        var lowerPriority = Item("train", QueueStatus.Pending, 0);
        lowerPriority.Group = QueueGroup.TroopTraining;
        lowerPriority.Priority = 1;
        var higherPriority = Item("upgrade", QueueStatus.Pending, 0);
        higherPriority.Group = QueueGroup.TroopTraining;
        higherPriority.Priority = 5;
        var disabledVillage = Item("disabled", QueueStatus.Pending, 0);
        disabledVillage.Group = QueueGroup.TroopTraining;

        var plan = ContinuousLoopSelector.CreatePlan(new ContinuousLoopSelectionInput(
            [Candidate(lowerPriority, "a"), Candidate(disabledVillage, "b", allowed: false), Candidate(higherPriority, "a")],
            [QueueGroup.TroopTraining]));

        Assert.Equal([higherPriority, lowerPriority], plan.OrderedItemsByGroup[QueueGroup.TroopTraining]);
    }

    [Fact]
    public void CreatePlan_PrefersReadyUtilityForActiveVillageAndKeepsFallback()
    {
        var otherVillage = Item("collect_tasks", QueueStatus.Pending, 0);
        var activeVillage = Item("collect_daily_quests", QueueStatus.Pending, 0);
        activeVillage.Priority = -1;

        var selection = ContinuousLoopSelector.SelectUtility(new ContinuousLoopUtilitySelectionInput(
            [Candidate(otherVillage, "b", utilityEnabled: true), Candidate(activeVillage, "a", utilityEnabled: true)],
            "a",
            Now));

        Assert.Same(activeVillage, selection.PreferredItem);
        Assert.Equal([otherVillage, activeVillage], selection.ReadyItems);
    }

    [Fact]
    public void CreatePlan_PreviewAndLiveInputsProduceSamePlanWithoutMutation()
    {
        var item = Item("hero_manage", QueueStatus.Pending, 0);
        item.Group = QueueGroup.Hero;
        var input = new ContinuousLoopSelectionInput(
            [Candidate(item, null)],
            [QueueGroup.Hero]);

        var preview = ContinuousLoopSelector.CreatePlan(input);
        var live = ContinuousLoopSelector.CreatePlan(input);

        Assert.Equal(preview.OrderedGroups, live.OrderedGroups);
        Assert.Equal(preview.OrderedItemsByGroup[QueueGroup.Hero], live.OrderedItemsByGroup[QueueGroup.Hero]);
        Assert.Equal(QueueStatus.Pending, item.Status);
    }

    [Fact]
    public void SelectNonConstructionGroup_RotatesPastDeferredVillage()
    {
        var deferred = Item("train-a", QueueStatus.Pending, 60);
        var ready = Item("train-b", QueueStatus.Pending, 0);
        var villageKeys = new Dictionary<Guid, string?>
        {
            [deferred.Id] = "a",
            [ready.Id] = "b",
        };

        var result = ContinuousLoopSelector.SelectNonConstructionGroup(
            new ContinuousLoopGroupSelectionInput(
                QueueGroup.TroopTraining,
                [deferred, ready],
                "a",
                Now,
                villageKeys));

        Assert.Same(ready, result.Item);
        Assert.Equal("b", result.RotationVillageKey);
    }

    [Fact]
    public void SelectNonConstructionGroup_PreservesHeroAttributeException()
    {
        var deferredHero = Item("hero_manage", QueueStatus.Pending, 60);
        var attributeTask = Item("spend_hero_attribute_points", QueueStatus.Pending, 0);
        var villageKeys = new Dictionary<Guid, string?>
        {
            [deferredHero.Id] = null,
            [attributeTask.Id] = null,
        };

        var result = ContinuousLoopSelector.SelectNonConstructionGroup(
            new ContinuousLoopGroupSelectionInput(
                QueueGroup.Hero,
                [deferredHero, attributeTask],
                null,
                Now,
                villageKeys));

        Assert.Same(attributeTask, result.Item);
    }

    private static ContinuousLoopSelectionCandidate Candidate(
        QueueItem item,
        string? villageKey,
        bool allowed = true,
        bool utilityEnabled = false)
        => new(item, villageKey, allowed, utilityEnabled);

    private static QueueItem Item(string taskName, QueueStatus status, int secondsUntilReady)
        => new()
        {
            Id = Guid.NewGuid(),
            TaskName = taskName,
            Status = status,
            NextAttemptAt = Now.AddSeconds(secondsUntilReady),
        };
}
