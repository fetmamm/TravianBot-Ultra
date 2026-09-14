using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationMissingBuildingUpgradeRecoveryTests
{
    [Fact]
    public async Task NonMissingBuildingOutcome_IsNotHandled()
    {
        var port = new InMemoryPort();
        var recovery = new AutomationMissingBuildingUpgradeRecovery(port);

        var handled = await recovery.TryRecoverAsync(
            CreateUpgrade("Academy"),
            new BotOptions(),
            BotTaskExecutionResult.Empty,
            "[LOOP 1]",
            Stopwatch.StartNew(),
            default);

        Assert.False(handled);
        Assert.Empty(port.Trace);
    }

    [Fact]
    public async Task UnknownBuilding_DefersWithoutLiveRead()
    {
        var port = new InMemoryPort();
        var recovery = new AutomationMissingBuildingUpgradeRecovery(port);

        var handled = await recovery.TryRecoverAsync(
            CreateUpgrade("Unknown building"),
            new BotOptions(),
            MissingBuildingResult(),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            default);

        Assert.True(handled);
        Assert.Equal(TimeSpan.FromMinutes(1), port.DeferredDelay);
        Assert.Equal(["defer"], port.Trace);
    }

    [Fact]
    public async Task LiveTargetAlreadySatisfied_RemovesStaleUpgrade()
    {
        var port = new InMemoryPort
        {
            LiveStatus = new VillageStatus(
                "Alpha",
                [],
                new Dictionary<string, string>(),
                [],
                [new Building(37, "Academy", 5, "/build.php?id=37", 22)],
                []),
        };
        var recovery = new AutomationMissingBuildingUpgradeRecovery(port);

        var handled = await recovery.TryRecoverAsync(
            CreateUpgrade("Academy"),
            new BotOptions(),
            MissingBuildingResult(),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            default);

        Assert.True(handled);
        Assert.Equal(
            ["read", "apply-live", "succeeded", "removed", "reconcile", "refresh-queue"],
            port.Trace);
        Assert.Contains("removed stale upgrade", port.Logs[^1]);
    }

    private static QueueItem CreateUpgrade(string name) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = "upgrade_building_to_level",
        Status = QueueStatus.Running,
        Priority = 10,
        Payload = new BuildingUpgradePayload(38, 5, name).ToDictionary(),
    };

    private static BotTaskExecutionResult MissingBuildingResult() => new(
        [new BotTaskResult("upgrade_building_to_level", "slot is empty", ConstructionTaskOutcome.MissingBuilding)]);

    private sealed class InMemoryPort : IAutomationMissingBuildingUpgradeRecoveryPort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public TimeSpan? DeferredDelay { get; private set; }
        public VillageStatus LiveStatus { get; init; } = new(
            "Alpha",
            [],
            new Dictionary<string, string>(),
            [],
            [],
            []);

        public string? GetVillageName(QueueItem item) => "Alpha";
        public ValueTask<VillageStatus> ReadLiveVillageStatusAsync(
            BotOptions options,
            QueueItem item,
            CancellationToken cancellationToken)
        {
            Trace.Add("read");
            return ValueTask.FromResult(LiveStatus);
        }
        public void ApplyLiveVillageStatus(VillageStatus status, string? villageName) => Trace.Add("apply-live");
        public void ReconcileLiveQueue(VillageStatus status) => Trace.Add("reconcile");
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            DeferredDelay = delay;
            Trace.Add("defer");
            return true;
        }
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
        public bool UpdatePendingQueueItem(
            Guid itemId,
            Dictionary<string, string> payload,
            int? priority,
            TimeSpan delay) => true;
        public IReadOnlyList<QueueItem> GetQueueItems() => [];
        public bool IsSameVillageOrGlobal(QueueItem source, QueueItem candidate) => true;
        public QueueItem Enqueue(
            string taskName,
            Dictionary<string, string> payload,
            int priority,
            int maxRetries) => new() { Id = Guid.NewGuid(), TaskName = taskName, Payload = payload };
        public void RequestQueueUiRefresh(Guid? selectedItemId = null) => Trace.Add("refresh-queue");
        public ValueTask RefreshVillageActivityIndicatorsAsync() => ValueTask.CompletedTask;
        public void Log(string message) => Logs.Add(message);
    }
}
