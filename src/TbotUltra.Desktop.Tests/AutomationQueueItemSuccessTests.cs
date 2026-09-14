using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueItemSuccessTests
{
    [Fact]
    public async Task ExistingConstruct_IsRemovedWithoutRefresh()
    {
        var port = new InMemoryPort();
        var item = Item("construct_building");
        var result = new BotTaskExecutionResult(
            [new BotTaskResult(item.TaskName, "already exists", ConstructionTaskOutcome.AlreadyExists)]);

        var refreshed = await new AutomationQueueItemSuccess(port).HandleAsync(
            item,
            new BotOptions(),
            result,
            default);

        Assert.False(refreshed);
        Assert.Equal(["succeeded", "removed", "refresh-queue"], port.Trace);
    }

    [Fact]
    public async Task BuildingMutation_RefreshesConstructionAndStorageDependency()
    {
        var port = new InMemoryPort
        {
            ConstructionRefresh = new QueueItemSuccessRefreshResult(true, false),
        };

        var refreshed = await new AutomationQueueItemSuccess(port).HandleAsync(
            Item("upgrade_building_to_level"),
            new BotOptions(),
            BotTaskExecutionResult.Empty,
            default);

        Assert.True(refreshed);
        Assert.Equal(
            ["succeeded", "refresh-construction", "refresh-storage", "storage-succeeded"],
            port.Trace);
    }

    [Fact]
    public async Task ReinforcementSuccess_SchedulesNextSend()
    {
        var port = new InMemoryPort();

        await new AutomationQueueItemSuccess(port).HandleAsync(
            Item("send_reinforcements_between_villages"),
            new BotOptions(),
            BotTaskExecutionResult.Empty,
            default);

        Assert.Equal(["succeeded", "schedule-reinforcement"], port.Trace);
    }

    private static QueueItem Item(string taskName) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Payload = taskName.StartsWith("upgrade_building", StringComparison.Ordinal)
            ? new BuildingUpgradePayload(38, 5, "Academy").ToDictionary()
            : [],
    };

    private sealed class InMemoryPort : IAutomationQueueItemSuccessPort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public QueueItemSuccessRefreshResult ConstructionRefresh { get; init; }
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
        public void RebindPendingBuildingUpgrades(QueueItem item, int liveSlotId) =>
            Trace.Add("rebind-upgrades");
        public void RebindPendingBuildingTemplateStep(QueueItem item, int liveSlotId) =>
            Trace.Add("rebind-template");
        public void RequestQueueUiRefresh() => Trace.Add("refresh-queue");
        public ValueTask<bool> RefreshResourceStatusAsync(CancellationToken cancellationToken)
        {
            Trace.Add("refresh-resource");
            return ValueTask.FromResult(true);
        }
        public ValueTask<QueueItemSuccessRefreshResult> RefreshConstructionStatusAsync(
            QueueItem item,
            CancellationToken cancellationToken)
        {
            Trace.Add("refresh-construction");
            return ValueTask.FromResult(ConstructionRefresh);
        }
        public ValueTask RefreshCurrentPageStorageStatusAsync(
            BotOptions options,
            string reason,
            CancellationToken cancellationToken)
        {
            Trace.Add("refresh-storage");
            return ValueTask.CompletedTask;
        }
        public ValueTask HandleStorageDependencySucceededAsync(QueueItem item)
        {
            Trace.Add("storage-succeeded");
            return ValueTask.CompletedTask;
        }
        public ValueTask HandleCropShortageRecoveryStepSucceededAsync(QueueItem item) => ValueTask.CompletedTask;
        public ValueTask RefreshHeroAsync(BotOptions options, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask RefreshTroopTrainingAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RefreshBreweryAsync(
            QueueItem item,
            BotOptions options,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void ScheduleNextReinforcementSend(BotOptions options) => Trace.Add("schedule-reinforcement");
        public void ApplyProductionBonusResult(string? message) => Trace.Add("production-bonus");
        public void ApplyDailyResetResult(string? message) => Trace.Add("daily-reset");
        public void Log(string message) => Logs.Add(message);
    }
}
