using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationConstructLiveReconciliationTests
{
    [Fact]
    public void ExistingBuilding_RemovesStaleConstructBeforeWorkerExecution()
    {
        var item = Construct(new BuildingConstructPayload(38, 22, "Academy"));
        var port = new InMemoryPort();
        var reconciliation = new AutomationConstructLiveReconciliation(port);

        var handled = reconciliation.TryHandleExistingConstruct(
            item,
            Status(new Building(37, "Academy", 1, "/build.php?id=37", 22)),
            "[LOOP 1]",
            Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal(
            ["reconcile", "rebind-upgrades:37", "reconcile", "succeeded", "removed", "refresh"],
            port.Trace);
    }

    [Fact]
    public void CompositeConstruct_RebindsAndDefersSameItem()
    {
        var item = Construct(new BuildingConstructPayload(38, 22, "Academy", 5));
        var port = new InMemoryPort();
        var reconciliation = new AutomationConstructLiveReconciliation(port);

        var handled = reconciliation.TryHandleExistingConstruct(
            item,
            Status(new Building(37, "Academy", 1, "/build.php?id=37", 22)),
            "[LOOP 1]",
            Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal("37", item.Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
        Assert.Equal(
            ["reconcile", "rebind-upgrades:37", "defer", "patch", "rebind-template:37", "refresh"],
            port.Trace);
    }

    [Fact]
    public void OccupiedSlot_RebindsChainToConfirmedEmptySlot()
    {
        var item = Construct(new BuildingConstructPayload(38, 22, "Academy"));
        var buildings = Enumerable.Range(19, 22)
            .Select(slot => slot == 38
                ? new Building(slot, "Marketplace", 1, $"/build.php?id={slot}", 17)
                : new Building(slot, "Empty", 0, $"/build.php?id={slot}", 0))
            .ToList();
        var port = new InMemoryPort { QueueItems = [item] };
        var reconciliation = new AutomationConstructLiveReconciliation(port);

        var handled = reconciliation.TryHandleOccupiedSlot(
            item,
            Status(buildings),
            "[LOOP 1]",
            Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal("19", item.Payload[BotOptionPayloadKeys.BuildingConstructSlotId]);
        Assert.Equal(["defer", "apply", "refresh"], port.Trace);
    }

    [Fact]
    public void OccupiedSlot_WithCompleteFullVillage_FailsPermanentlyWithoutDeferring()
    {
        var item = Construct(new BuildingConstructPayload(38, 22, "Academy"));
        var buildings = Enumerable.Range(19, 22)
            .Select(slot => slot == 38
                ? new Building(slot, "Marketplace", 1, $"/build.php?id={slot}", 17)
                : new Building(slot, "Warehouse", 1, $"/build.php?id={slot}", 10))
            .ToList();
        var port = new InMemoryPort { QueueItems = [item] };

        var handled = new AutomationConstructLiveReconciliation(port).TryHandleOccupiedSlot(
            item,
            Status(buildings),
            "[LOOP 1]",
            Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal(["permanent-failure", "forget"], port.Trace);
        Assert.DoesNotContain("defer", port.Trace);
        Assert.Contains(port.Logs, log => log.StartsWith("ALARM:", StringComparison.Ordinal));
        Assert.Contains(port.Logs, log => log.Contains("moved to History", StringComparison.Ordinal));
    }

    private static QueueItem Construct(BuildingConstructPayload payload) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = "construct_building",
        Status = QueueStatus.Running,
        Payload = payload.ToDictionary(),
    };

    private static VillageStatus Status(params Building[] buildings) => Status((IReadOnlyList<Building>)buildings);

    private static VillageStatus Status(IReadOnlyList<Building> buildings) => new(
        "Alpha",
        [],
        new Dictionary<string, string>(),
        [],
        buildings,
        []);

    private sealed class InMemoryPort : IAutomationConstructLiveReconciliationPort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public IReadOnlyList<QueueItem> QueueItems { get; init; } = [];
        public void ReconcileLiveQueue(VillageStatus status) => Trace.Add("reconcile");
        public void RebindPendingUpgrades(QueueItem source, int liveSlotId) =>
            Trace.Add($"rebind-upgrades:{liveSlotId}");
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            Trace.Add("defer");
            return true;
        }
        public bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload)
        {
            Trace.Add("patch");
            return true;
        }
        public void RebindPendingTemplateStep(QueueItem source, int liveSlotId) =>
            Trace.Add($"rebind-template:{liveSlotId}");
        public bool MarkSucceeded(Guid itemId)
        {
            Trace.Add("succeeded");
            return true;
        }
        public bool RemoveQueueItem(Guid itemId)
        {
            Trace.Add("removed");
            return true;
        }
        public IReadOnlyList<QueueItem> GetSameVillageQueueItems(QueueItem source) => QueueItems;
        public bool MarkPermanentlyFailed(Guid itemId)
        {
            Trace.Add("permanent-failure");
            return true;
        }
        public void ForgetBuildingQueueCaches(QueueItem item) => Trace.Add("forget");
        public bool ApplyPendingQueueReconciliation(IReadOnlyList<QueuePayloadUpdate> updates)
        {
            Trace.Add("apply");
            return true;
        }
        public string? GetVillageName(QueueItem item) => "Alpha";
        public void RequestQueueUiRefresh() => Trace.Add("refresh");
        public void Log(string message) => Logs.Add(message);
    }
}
