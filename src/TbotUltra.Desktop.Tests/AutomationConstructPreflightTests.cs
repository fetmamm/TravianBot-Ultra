using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationConstructPreflightTests
{
    [Fact]
    public async Task NonConstruct_SkipsLiveRead()
    {
        var port = new InMemoryPort();

        var result = await new AutomationConstructPreflight(port).RefreshTargetStatusAsync(
            Item("collect_tasks"),
            new BotOptions(),
            default);

        Assert.True(result.CanUseCache);
        Assert.Null(result.FreshStatus);
        Assert.Empty(port.Trace);
    }

    [Fact]
    public async Task ConstructTarget_ReadsAndPublishesLiveStatus()
    {
        var port = new InMemoryPort { TargetVillageName = "Alpha" };

        var result = await new AutomationConstructPreflight(port).RefreshTargetStatusAsync(
            Item("construct_building"),
            new BotOptions(),
            default);

        Assert.True(result.CanUseCache);
        Assert.Same(port.Status, result.FreshStatus);
        Assert.Equal(["read", "apply"], port.Trace);
    }

    [Fact]
    public async Task FullBuildingQueue_DefersBeforeRequirementRepair()
    {
        var status = Status() with
        {
            IsBuildingInProgress = true,
            ActiveBuildCount = 1,
            BuildQueueRemainingSeconds = 900,
            ActiveConstructions =
            [
                new ActiveConstruction(
                    ConstructionKind.Building,
                    "Warehouse",
                    4,
                    900,
                    "00:15:00"),
            ],
            ActiveConstructionsFromOverview = true,
        };
        var port = new InMemoryPort { CachedStatus = status };
        var item = Item("construct_building");

        var handled = await new AutomationConstructPreflight(port).TryHandleQueueFullAsync(
            item,
            "[LOOP 1]",
            Stopwatch.StartNew());

        Assert.True(handled);
        Assert.Equal(TimeSpan.FromSeconds(900), port.DeferredDelay);
        Assert.Equal(
            BotOptionPayloadKeys.UpgradeDeferReasonQueueFull,
            item.Payload[BotOptionPayloadKeys.UpgradeDeferReason]);
        Assert.Equal(["clear-fill", "defer", "patch", "refresh-indicators"], port.Trace);
    }

    private static QueueItem Item(string taskName) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Status = QueueStatus.Running,
        Payload = taskName == "construct_building"
            ? new BuildingConstructPayload(38, 22, "Academy").ToDictionary()
            : [],
    };

    private static VillageStatus Status() => new(
        "Alpha",
        [],
        new Dictionary<string, string>(),
        [],
        [],
        []);

    private sealed class InMemoryPort : IAutomationConstructPreflightPort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public string? TargetVillageName { get; init; }
        public VillageStatus Status { get; init; } = AutomationConstructPreflightTests.Status();
        public VillageStatus? CachedStatus { get; init; }
        public TimeSpan? DeferredDelay { get; private set; }
        public string? GetTargetVillageName(QueueItem item) => TargetVillageName;
        public string? GetTargetVillageUrl(QueueItem item) => null;
        public string? GetTargetVillageKey(QueueItem item) => "1:2";
        public ValueTask<VillageStatus> ReadLiveVillageStatusAsync(
            BotOptions options,
            string? villageName,
            string? villageUrl,
            CancellationToken cancellationToken)
        {
            Trace.Add("read");
            return ValueTask.FromResult(Status);
        }
        public void ApplyLiveVillageStatus(VillageStatus status, string? villageName) => Trace.Add("apply");
        public VillageStatus? GetCachedBuildingStatus(QueueItem item) => CachedStatus;
        public bool? TravianPlusActive => false;
        public void ClearLoginFillForFullSlots(VillageStatus status, string? villageKey, string source) =>
            Trace.Add("clear-fill");
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            DeferredDelay = delay;
            Trace.Add("defer");
            return true;
        }
        public bool PatchDeferred(
            Guid itemId,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyCollection<string> keysToRemove,
            TimeSpan delay)
        {
            Trace.Add("patch");
            return true;
        }
        public string? GetVillageName(QueueItem item) => "Alpha";
        public string FormatServerTime(DateTimeOffset value) => value.ToString("O");
        public ValueTask RefreshVillageActivityIndicatorsAsync()
        {
            Trace.Add("refresh-indicators");
            return ValueTask.CompletedTask;
        }
        public void Log(string message) => Logs.Add(message);
    }
}
