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

    [Fact]
    public async Task CompleteCachedIronFoundryBlock_QueuesSpecificIronMineWithoutLivePreflight()
    {
        var parent = Item(
            "construct_building",
            new BuildingConstructPayload(30, 7, "Iron Foundry").ToDictionary());
        var bulk = Item(
            "upgrade_all_resources_to_level",
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "10",
            });
        bulk.Status = QueueStatus.Pending;
        var buildings = Enumerable.Range(19, 22)
            .Select(slot => new Building(slot, "Empty", 0, null, null))
            .ToList();
        buildings[7] = new Building(26, "Main Building", 15, null, 15);
        var fields = Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(
                slot,
                slot is 7 or 10 or 11 or 17 ? "Iron Mine" : "Cropland",
                slot is 7 or 10 or 11 or 17 ? "iron" : "crop",
                9,
                null))
            .ToList();
        var status = new VillageStatus(
            "ROMA",
            [],
            new Dictionary<string, string>(),
            fields,
            buildings,
            [],
            Tribe: "Romans",
            VillageCount: 1,
            ActiveConstructions: [],
            ActiveConstructionsFromOverview: true);
        var port = new InMemoryPort
        {
            Context = new AutomationConstructionRequirementContext(status, [bulk]),
            QueueItems = [bulk],
        };

        var handled = await new AutomationConstructionRequirementGuard(port, new FixedTimeProvider(Now))
            .TryHandleAsync(parent, "[LOOP 1]", Stopwatch.StartNew(), requireCompleteSnapshot: true);

        Assert.True(handled);
        var repair = Assert.Single(port.Enqueued);
        Assert.Equal("upgrade_resource_to_level", repair.TaskName);
        Assert.Equal("7", repair.Payload[BotOptionPayloadKeys.ResourceUpgradeSlotId]);
        Assert.Equal("10", repair.Payload[BotOptionPayloadKeys.ResourceUpgradeTargetLevel]);
        Assert.Equal(0, port.UpdatePendingCalls);
    }

    [Fact]
    public async Task IncompleteCachedSnapshot_DoesNotCreatePrerequisiteRepair()
    {
        var parent = Item(
            "construct_building",
            new BuildingConstructPayload(30, 7, "Iron Foundry").ToDictionary());
        var status = new VillageStatus(
            "ROMA",
            [],
            new Dictionary<string, string>(),
            [new ResourceField(7, "Iron Mine", "iron", 9, null)],
            [new Building(26, "Main Building", 15, null, 15)],
            [],
            Tribe: "Romans");
        var port = new InMemoryPort
        {
            Context = new AutomationConstructionRequirementContext(status, []),
        };

        var handled = await new AutomationConstructionRequirementGuard(port, new FixedTimeProvider(Now))
            .TryHandleAsync(parent, "[LOOP 1]", Stopwatch.StartNew(), requireCompleteSnapshot: true);

        Assert.False(handled);
        Assert.Empty(port.Enqueued);
        Assert.Null(port.DeferredDelay);
    }

    [Fact]
    public async Task PrioritizedSpecificRepair_PreservesItsDeadlineAndDoesNotPromoteAgain()
    {
        var parent = Item(
            "construct_building",
            new BuildingConstructPayload(30, 7, "Iron Foundry").ToDictionary());
        parent.Priority = 0;
        var repair = Item(
            "upgrade_resource_to_level",
            new ResourceUpgradePayload(7, 10, "Iron Mine").ToDictionary());
        repair.Status = QueueStatus.Pending;
        repair.Priority = 10;
        repair.NextAttemptAt = Now.AddMinutes(12);
        var status = new VillageStatus(
            "ROMA",
            [],
            new Dictionary<string, string>(),
            [new ResourceField(7, "Iron Mine", "iron", 9, null)],
            [new Building(26, "Main Building", 15, null, 15)],
            [],
            Tribe: "Romans");
        var port = new InMemoryPort
        {
            Context = new AutomationConstructionRequirementContext(status, [repair]),
            QueueItems = [repair],
        };

        var handled = await new AutomationConstructionRequirementGuard(port, new FixedTimeProvider(Now))
            .TryHandleAsync(parent, "[LOOP 1]", Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal(TimeSpan.FromMinutes(12), port.DeferredDelay);
        Assert.Equal(0, port.UpdatePendingCalls);
        Assert.Empty(port.Enqueued);
        Assert.Contains(port.Logs, line => line.Contains("preserving its priority and retry deadline", StringComparison.Ordinal));
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
        public List<QueueItem> Enqueued { get; } = [];
        public AutomationConstructionRequirementContext Context { get; init; } = new(null, []);
        public IReadOnlyList<QueueItem> QueueItems { get; init; } = [];
        public TimeSpan? DeferredDelay { get; private set; }
        public int UpdatePendingCalls { get; private set; }
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
        public IReadOnlyList<QueueItem> GetQueueItems() => QueueItems;
        public bool UpdatePendingQueueItem(
            Guid itemId,
            Dictionary<string, string> payload,
            int priority,
            TimeSpan delay)
        {
            UpdatePendingCalls++;
            return true;
        }
        public QueueItem Enqueue(
            string taskName,
            Dictionary<string, string> payload,
            int priority,
            int maxRetries)
        {
            var item = new QueueItem
            {
                Id = Guid.NewGuid(),
                TaskName = taskName,
                Payload = payload,
                Priority = priority,
                Status = QueueStatus.Pending,
                NextAttemptAt = Now,
            };
            Enqueued.Add(item);
            return item;
        }
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
