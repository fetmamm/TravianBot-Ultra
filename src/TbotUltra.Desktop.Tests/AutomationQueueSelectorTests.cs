using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Select_UrgentWorkPreemptsTheCurrentVillageBatch()
    {
        var current = Candidate("current", "a", priority: 0);
        var urgent = Candidate("urgent", "b", priority: 100);

        var result = Select([current, urgent], new VillageBatchSnapshot("a", 1));

        Assert.Equal(urgent.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.UrgentPreemption, result.Reason);
    }

    [Fact]
    public void Select_KeepsReadyWorkInTheCurrentVillage()
    {
        var current = Candidate("current", "a");
        var other = Candidate("other", "b");

        var result = Select([current, other], new VillageBatchSnapshot("a", 1));

        Assert.Equal(current.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.Selected, result.Reason);
    }

    [Fact]
    public void Select_DoesNotRotateAwayFromReadyCurrentVillageAfterManyAttempts()
    {
        var current = Candidate("current", "a");
        var other = Candidate("other", "b");

        var result = Select(
            [current, other],
            new VillageBatchSnapshot("a", 100));

        Assert.Equal(current.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.Selected, result.Reason);
    }

    [Fact]
    public void Select_NonUrgentUtilityDoesNotInterruptReadyCurrentVillageWork()
    {
        var current = Candidate("current", "a");
        var utility = Candidate(
            "collect_tasks",
            "b",
            group: QueueGroup.Farming,
            isUtilityEnabled: true);

        var result = Select([utility, current], new VillageBatchSnapshot("a", 1));

        Assert.Equal(current.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.Selected, result.Reason);
    }

    [Fact]
    public void Select_UrgentUtilityStillPreemptsReadyCurrentVillageWork()
    {
        var current = Candidate("current", "a");
        var utility = Candidate(
            "collect_tasks",
            "b",
            group: QueueGroup.Account,
            isUtilityEnabled: true);

        var result = Select([current, utility], new VillageBatchSnapshot("a", 1));

        Assert.Equal(utility.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.UrgentPreemption, result.Reason);
    }

    [Fact]
    public void Select_ResumesInterruptedVillageAfterUrgentWorkCompletes()
    {
        var interrupted = Candidate("interrupted", "a");
        var urgentVillage = Candidate("normal-in-urgent-village", "b");

        var result = Select(
            [urgentVillage, interrupted],
            new VillageBatchSnapshot(
                "a",
                2,
                UrgentTargetVillageKey: "b",
                HasUrgentPreemption: true),
            activeVillageKey: "b");

        Assert.Equal(interrupted.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.UrgentResume, result.Reason);
        Assert.False(result.CompleteUrgentPreemption);
    }

    [Fact]
    public void Select_CompletesUrgentPreemptionWhenInterruptedVillageHasNoReadyWork()
    {
        var other = Candidate("other", "b");

        var result = Select(
            [other],
            new VillageBatchSnapshot(
                "a",
                2,
                UrgentTargetVillageKey: "b",
                HasUrgentPreemption: true),
            activeVillageKey: "b");

        Assert.Equal(other.Item.Id, result.Selected?.Id);
        Assert.True(result.CompleteUrgentPreemption);
    }

    [Fact]
    public void Select_ReadyCrossVillageWorkWinsBeforeAShortCurrentVillageHold()
    {
        var deferredCurrent = Candidate("deferred", "a", nextAttemptAt: Now.AddSeconds(10));
        var readyOther = Candidate("other", "b");

        var result = Select([deferredCurrent, readyOther], new VillageBatchSnapshot("a", 1));

        Assert.Equal(readyOther.Item.Id, result.Selected?.Id);
        Assert.Equal(AutomationQueueSelectionReason.VillageRotationNoReadyWork, result.Reason);
    }

    [Fact]
    public void Select_HoldsTheCurrentVillageOnlyWhenNoReadyWorkExistsElsewhere()
    {
        var deferredCurrent = Candidate("deferred", "a", nextAttemptAt: Now.AddSeconds(10));

        var result = Select([deferredCurrent], new VillageBatchSnapshot("a", 1));

        Assert.Null(result.Selected);
        Assert.Equal(AutomationQueueSelectionReason.ShortVillageHold, result.Reason);
        Assert.Equal(Now.AddSeconds(10), result.HoldUntil);
    }

    [Fact]
    public void Select_RemoteConstructionCommitsDelayBeforeChangingVillage()
    {
        var remote = Candidate("upgrade_building_to_level", "b");
        var calls = new List<bool>();

        var result = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput([remote], [QueueGroup.Construction],
                new VillageBatchSnapshot("a", 1), "a", Now, 30, Preview: false),
            (items, now, preview) =>
            {
                calls.Add(preview);
                if (!preview)
                {
                    items[0].NextAttemptAt = now.AddMinutes(5);
                    return null;
                }

                return ContinuousLoopSelector.SelectReadyGroupHead(items, now);
            });

        Assert.Null(result.Selected);
        Assert.Equal(Now.AddMinutes(5), remote.Item.NextAttemptAt);
        Assert.Contains(false, calls);
    }

    [Fact]
    public void Select_RemoteDelayLetsAnotherVillageRunWithoutSkippingItsQueueHead()
    {
        var delayed = Candidate("upgrade_building_to_level", "b", priority: 10);
        var laterInSameVillage = Candidate("upgrade_building_to_level", "b");
        var other = Candidate("upgrade_building_to_level", "c");

        var result = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput([delayed, laterInSameVillage, other],
                [QueueGroup.Construction], new VillageBatchSnapshot("a", 1), "a", Now, 30, Preview: false),
            (items, now, preview) =>
            {
                if (items[0].Id == delayed.Item.Id && !preview)
                {
                    items[0].NextAttemptAt = now.AddMinutes(5);
                    return null;
                }

                return ContinuousLoopSelector.SelectReadyGroupHead(items, now);
            });

        Assert.Same(other.Item, result.Selected);
    }

    [Fact]
    public void Select_RemoteFullQueueIsPreparedWithoutNavigating()
    {
        var blocked = Candidate("upgrade_building_to_level", "b");
        var committed = false;

        var result = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput([blocked], [QueueGroup.Construction],
                new VillageBatchSnapshot("a", 1), "a", Now, 30, Preview: false),
            (items, now, preview) =>
            {
                if (!preview)
                {
                    committed = true;
                    items[0].NextAttemptAt = now.AddMinutes(10);
                }

                return null; // Full queue: no ready item even in the preview.
            });

        Assert.Null(result.Selected);
        Assert.True(committed);
        Assert.Equal(Now.AddMinutes(10), blocked.Item.NextAttemptAt);
    }

    [Fact]
    public void Select_PreviewDoesNotCommitRemoteConstruction()
    {
        var remote = Candidate("upgrade_building_to_level", "b");
        var commits = 0;

        var result = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput([remote], [QueueGroup.Construction],
                new VillageBatchSnapshot("a", 1), "a", Now, 30, Preview: true),
            (items, now, preview) =>
            {
                if (!preview) commits++;
                return ContinuousLoopSelector.SelectReadyGroupHead(items, now);
            });

        Assert.Same(remote.Item, result.Selected);
        Assert.Equal(0, commits);
    }

    private static AutomationQueueSelectionResult Select(
        IReadOnlyList<ContinuousLoopSelectionCandidate> candidates,
        VillageBatchSnapshot batch,
        string activeVillageKey = "a") => AutomationQueueSelector.Select(
        new AutomationQueueSelectionInput(
            candidates,
            [QueueGroup.Construction],
            batch,
            activeVillageKey,
            Now,
            ShortVillageDeferSeconds: 30,
            Preview: false),
        (items, now, _) => ContinuousLoopSelector.SelectReadyGroupHead(items, now));

    private static ContinuousLoopSelectionCandidate Candidate(
        string taskName,
        string villageKey,
        int priority = 0,
        DateTimeOffset? nextAttemptAt = null,
        QueueGroup group = QueueGroup.Construction,
        bool isUtilityEnabled = false)
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = taskName,
            Group = group,
            Priority = priority,
            Status = QueueStatus.Pending,
            CreatedAt = Now,
            UpdatedAt = Now,
            NextAttemptAt = nextAttemptAt ?? Now,
        };
        return new ContinuousLoopSelectionCandidate(item, villageKey, true, isUtilityEnabled);
    }
}
