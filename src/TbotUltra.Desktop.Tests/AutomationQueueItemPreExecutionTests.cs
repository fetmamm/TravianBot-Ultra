using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueItemPreExecutionTests
{
    [Fact]
    public async Task QueuedConstructDependency_ShortCircuitsBeforeLivePreflight()
    {
        var policies = new InMemoryPolicies { UpgradeWaitHandled = true };

        var result = await CreateSubject(policies).RunAsync(
            Item(), new BotOptions(), "[LOOP 1]", Stopwatch.StartNew(), default);

        Assert.Equal(new QueueItemGuardResult(true, false), result);
        Assert.Equal(["upgrade-wait"], policies.Trace);
    }

    [Fact]
    public async Task ExistingLiveConstruct_ShortCircuitsBeforeSlotAndRequirementChecks()
    {
        var policies = new InMemoryPolicies
        {
            Observation = new ConstructPreflightObservation(true, Status()),
            ExistingConstructHandled = true,
        };

        var result = await CreateSubject(policies).RunAsync(
            Item(), new BotOptions(), "[LOOP 1]", Stopwatch.StartNew(), default);

        Assert.Equal(new QueueItemGuardResult(true, true), result);
        Assert.Equal(["upgrade-wait", "refresh", "existing"], policies.Trace);
    }

    [Fact]
    public async Task CachedStatus_RunsQueueFullBeforeRequirementGuard()
    {
        var policies = new InMemoryPolicies
        {
            Observation = new ConstructPreflightObservation(true, null),
            RequirementHandled = true,
        };

        var result = await CreateSubject(policies).RunAsync(
            Item(), new BotOptions(), "[LOOP 1]", Stopwatch.StartNew(), default);

        Assert.Equal(new QueueItemGuardResult(true, true), result);
        Assert.Equal(["upgrade-wait", "refresh", "queue-full", "requirements"], policies.Trace);
    }

    private static AutomationQueueItemPreExecution CreateSubject(InMemoryPolicies policies) =>
        new(policies, policies, policies);

    private static QueueItem Item() => new()
    {
        Id = Guid.NewGuid(),
        TaskName = "construct_building",
        Payload = [],
    };

    private static VillageStatus Status() => new(
        "Alpha",
        [],
        new Dictionary<string, string>(),
        [],
        [],
        []);

    private sealed class InMemoryPolicies :
        IAutomationConstructionRequirementGuard,
        IAutomationConstructLiveReconciliation,
        IAutomationConstructPreflight
    {
        public List<string> Trace { get; } = [];
        public bool UpgradeWaitHandled { get; init; }
        public bool ExistingConstructHandled { get; init; }
        public bool RequirementHandled { get; init; }
        public ConstructPreflightObservation Observation { get; init; } = new(true, null);

        public bool TryHandleUpgradeWaitingForConstruct(
            QueueItem item,
            string logPrefix,
            Stopwatch timer)
        {
            Trace.Add("upgrade-wait");
            return UpgradeWaitHandled;
        }

        public ValueTask<bool> TryHandleAsync(QueueItem item, string logPrefix, Stopwatch timer)
        {
            Trace.Add("requirements");
            return ValueTask.FromResult(RequirementHandled);
        }

        public ValueTask<ConstructPreflightObservation> RefreshTargetStatusAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken)
        {
            Trace.Add("refresh");
            return ValueTask.FromResult(Observation);
        }

        public ValueTask<bool> TryHandleQueueFullAsync(
            QueueItem item,
            string logPrefix,
            Stopwatch timer)
        {
            Trace.Add("queue-full");
            return ValueTask.FromResult(false);
        }

        public bool TryHandleExistingConstruct(
            QueueItem item,
            VillageStatus freshStatus,
            string logPrefix,
            Stopwatch timer)
        {
            Trace.Add("existing");
            return ExistingConstructHandled;
        }

        public bool TryHandleOccupiedSlot(
            QueueItem item,
            VillageStatus freshStatus,
            string logPrefix,
            Stopwatch timer)
        {
            Trace.Add("occupied");
            return false;
        }
    }
}
