using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationPassRuntimeTests
{
    [Fact]
    public void ImmediateWorkRequests_CoalesceUntilConsumed()
    {
        var runtime = new AutomationPassRuntime();

        runtime.RequestImmediateWork();
        runtime.RequestImmediateWork();

        Assert.True(runtime.IsImmediateWorkRequested);
        Assert.True(runtime.ConsumeImmediateWorkRequest());
        Assert.False(runtime.ConsumeImmediateWorkRequest());
        Assert.False(runtime.IsImmediateWorkRequested);
    }

    [Fact]
    public void ContinuousPassIds_AreMonotonicAndPublishTheCurrentPass()
    {
        var runtime = new AutomationPassRuntime();

        var first = runtime.BeginContinuousPass();
        var second = runtime.BeginContinuousPass();

        Assert.Equal(first + 1, second);
        Assert.Equal(second, runtime.CurrentContinuousPassId);
    }

    [Fact]
    public void AutoQueueRun_ResetsTheSharedVillageBatch()
    {
        var runtime = new AutomationPassRuntime();
        runtime.RecordVillageAttempt("a", "a");
        Assert.Equal(1, runtime.SnapshotVillageBatch("a").AttemptCount);

        runtime.BeginAutoQueueRun(42);

        Assert.Equal(42, runtime.AutoQueueRunLogId);
        Assert.Equal(0, runtime.SnapshotVillageBatch("a").AttemptCount);
    }

    [Fact]
    public void VillageBatch_IsSharedAcrossPassModesUntilExplicitlyReset()
    {
        var runtime = new AutomationPassRuntime();

        runtime.RecordVillageAttempt("a", "a");
        var observedByNextPass = runtime.SnapshotVillageBatch("a");

        Assert.Equal(new VillageBatchSnapshot("a", 1), observedByNextPass);
    }

    [Fact]
    public void UrgentPreemption_IsSharedWithTheNextContinuousPass()
    {
        var runtime = new AutomationPassRuntime();
        runtime.RecordVillageAttempt("a", "a");

        runtime.RecordUrgentPreemption("a", "b");
        runtime.ObserveVerifiedVillage("b");

        Assert.Equal("a", runtime.SnapshotVillageBatch("b").VillageKey);
    }

    [Fact]
    public void SmartSleepWakeState_IsOwnedByTheSharedRuntime()
    {
        var runtime = new AutomationPassRuntime();

        Assert.Equal(2, runtime.SmartSleepDeadlineGroups.Count);
        Assert.Contains(QueueGroup.Construction, runtime.SmartSleepDeadlineGroups);
        Assert.Contains(QueueGroup.Hero, runtime.SmartSleepDeadlineGroups);

        runtime.SetSmartSleepDeadlineGroups(new HashSet<QueueGroup> { QueueGroup.Construction });
        runtime.PrioritizeDeadlineWorkOnWake = true;

        Assert.True(runtime.PrioritizeDeadlineWorkOnWake);
        Assert.Equal([QueueGroup.Construction], runtime.SmartSleepDeadlineGroups);
    }
}
