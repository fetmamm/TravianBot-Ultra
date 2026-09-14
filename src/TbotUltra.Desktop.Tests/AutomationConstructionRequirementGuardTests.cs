using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationConstructionRequirementGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpgradeWaitingForQueuedConstruct_DefersBeforeWorkerExecution()
    {
        var upgrade = Item(
            "upgrade_building_to_level",
            new BuildingUpgradePayload(29, 10, "Cranny").ToDictionary());
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(29, 23, "Cranny").ToDictionary());
        construct.NextAttemptAt = Now.AddMinutes(3);
        var port = new InMemoryPort
        {
            Context = new AutomationConstructionRequirementContext(null, [construct]),
        };

        var handled = new AutomationConstructionRequirementGuard(port, new FixedTimeProvider(Now))
            .TryHandleUpgradeWaitingForConstruct(upgrade, "[LOOP 1]", Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal(TimeSpan.FromMinutes(3), port.DeferredDelay);
        Assert.Equal(["defer", "refresh-queue"], port.Trace);
    }

    [Fact]
    public async Task ActivePrerequisite_DefersWithClassifiedPayload()
    {
        var construct = Item(
            "construct_building",
            new BuildingConstructPayload(29, 20, "Stable").ToDictionary());
        var status = new VillageStatus(
            "Alpha",
            [],
            new Dictionary<string, string>(),
            [],
            [new Building(37, "Smithy", 3, "/build.php?id=37", 13)],
            [],
            ActiveConstructions:
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Academy",
                    5,
                    180,
                    "00:03:00",
                    TimerSnapshot.FromRemaining(180, Now)),
            ],
            ActiveConstructionsFromOverview: true);
        var port = new InMemoryPort
        {
            Context = new AutomationConstructionRequirementContext(status, []),
        };

        var handled = await new AutomationConstructionRequirementGuard(port, new FixedTimeProvider(Now))
            .TryHandleAsync(construct, "[LOOP 1]", Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal(TimeSpan.FromSeconds(190), port.DeferredDelay);
        Assert.Equal(
            BotOptionPayloadKeys.UpgradeDeferReasonRequirements,
            construct.Payload[BotOptionPayloadKeys.UpgradeDeferReason]);
        Assert.Equal(["defer", "patch", "refresh-indicators"], port.Trace);
    }

    private static QueueItem Item(string taskName, Dictionary<string, string> payload) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Status = QueueStatus.Running,
        NextAttemptAt = Now,
        Payload = payload,
    };

    private sealed class InMemoryPort : IAutomationConstructionRequirementGuardPort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public AutomationConstructionRequirementContext Context { get; init; } = new(null, []);
        public TimeSpan? DeferredDelay { get; private set; }
        public AutomationConstructionRequirementContext GetContext(QueueItem item) => Context;
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            DeferredDelay = delay;
            Trace.Add("defer");
            return true;
        }
        public bool PatchDeferred(
            Guid itemId,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyCollection<string> keysToRemove)
        {
            Trace.Add("patch");
            return true;
        }
        public bool MarkPermanentlyFailed(Guid itemId) => true;
        public IReadOnlyList<QueueItem> GetQueueItems() => [];
        public bool UpdatePendingQueueItem(
            Guid itemId,
            Dictionary<string, string> payload,
            int priority,
            TimeSpan delay) => true;
        public QueueItem Enqueue(
            string taskName,
            Dictionary<string, string> payload,
            int priority,
            int maxRetries) => new() { Id = Guid.NewGuid(), TaskName = taskName, Payload = payload };
        public void RequestQueueUiRefresh(Guid? selectedItemId = null) => Trace.Add("refresh-queue");
        public ValueTask RefreshVillageActivityIndicatorsAsync()
        {
            Trace.Add("refresh-indicators");
            return ValueTask.CompletedTask;
        }
        public void RaisePermanentFailureAlarm(QueueItem item, string message) => Trace.Add("alarm");
        public void Log(string message) => Logs.Add(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
