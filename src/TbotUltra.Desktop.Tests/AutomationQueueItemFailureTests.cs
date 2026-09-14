using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueItemFailureTests
{
    [Fact]
    public async Task TypedWait_DefersWithoutConsumingExecutionRetry()
    {
        var port = new InMemoryPort();
        var item = Item("collect_tasks");

        var shouldContinue = await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new TaskWaitException(45, "queued work"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.True(shouldContinue);
        Assert.Equal(TimeSpan.FromSeconds(45), port.DeferredDelay);
        Assert.Equal(["defer", "farm-refresh", "refresh-indicators"], port.Trace);
        Assert.DoesNotContain("failed", port.Trace);
    }

    [Fact]
    public async Task HeroAwayWait_PersistsTypedDeferReason()
    {
        var port = new InMemoryPort();
        var item = Item("hero_manage");

        await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new TaskWaitException(90, "hero away", TaskWaitReasons.HeroAway),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal("away", item.Payload["hero_defer_reason"]);
        Assert.Contains("update-payload", port.Trace);
    }

    [Fact]
    public async Task UnclassifiedFailure_ConsumesRetryAndRaisesTerminalAlarmWhenNeeded()
    {
        var port = new InMemoryPort();
        var item = Item("collect_tasks");

        var shouldContinue = await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new InvalidOperationException("boom"),
            "[AUTOQ 2]",
            Stopwatch.StartNew(),
            AutomationRunMode.AutoQueue);

        Assert.True(shouldContinue);
        Assert.Equal(["failed", "storage-failed", "alarm"], port.Trace);
        Assert.Contains("boom", port.Logs[^1]);
    }

    private static QueueItem Item(string taskName) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Status = QueueStatus.Running,
        Payload = [],
    };

    private sealed class InMemoryPort : IAutomationQueueItemFailurePort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public TimeSpan? DeferredDelay { get; private set; }
        public ValueTask<bool> TryHandleTroopsBlockedExecutionAsync(
            QueueItem item,
            Exception exception,
            string logPrefix) => ValueTask.FromResult(false);
        public bool TryHandleTownHallUnavailableExecution(
            QueueItem item,
            Exception exception,
            string logPrefix) => false;
        public ValueTask ApplyConstructionInlineWaitAsync(
            TimeSpan delay,
            string? humanizeVillageKey,
            TimeSpan? humanizeWait) => ValueTask.CompletedTask;
        public ValueTask ApplyHeroLowHpCooldownAsync(TimeSpan delay) => ValueTask.CompletedTask;
        public void ApplyBreweryCelebrationDeferSignal(string? message, TimeSpan delay) { }
        public void ApplyTownHallCelebrationDeferSignal(QueueItem item, string? message, TimeSpan delay) { }
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            DeferredDelay = delay;
            Trace.Add("defer");
            return true;
        }
        public string? GetVillageKey(QueueItem item) => "1:2";
        public string? GetVillageName(QueueItem item) => "Alpha";
        public void ClearConstructionLoginFillForBlockedHead(QueueItem item, string source) =>
            Trace.Add("clear-fill");
        public AutomationConstructionRequirementContext GetConstructionRequirementContext(QueueItem item) =>
            new(null, []);
        public bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload)
        {
            Trace.Add("patch");
            return true;
        }
        public bool MarkPermanentlyFailed(Guid itemId)
        {
            Trace.Add("permanent-failure");
            return true;
        }
        public void RaisePermanentFailureAlarm(QueueItem item, string message) => Trace.Add("alarm");
        public ValueTask RefreshVillageActivityIndicatorsAsync()
        {
            Trace.Add("refresh-indicators");
            return ValueTask.CompletedTask;
        }
        public string FormatServerTime(DateTimeOffset value) => value.ToString("O");
        public void RebindPendingTemplateStep(QueueItem item, int effectiveSlotId) => Trace.Add("rebind-template");
        public ValueTask HandleStorageCapacityDependencyAsync(
            QueueItem item,
            Dictionary<string, string> payload) => ValueTask.CompletedTask;
        public ValueTask RefreshFarmListsAfterAutoSendAsync(QueueItem item, string message)
        {
            Trace.Add("farm-refresh");
            return ValueTask.CompletedTask;
        }
        public ValueTask RefreshConstructionStatusAfterDeferAsync() => ValueTask.CompletedTask;
        public ValueTask HandleCropShortageDeferAsync(QueueItem item) => ValueTask.CompletedTask;
        public ValueTask RefreshTroopTrainingAfterBuildAsync(QueueItem item) => ValueTask.CompletedTask;
        public bool UpdateDeferredPayload(Guid itemId, Dictionary<string, string> payload)
        {
            Trace.Add("update-payload");
            return true;
        }
        public bool MarkExecutionFailed(Guid itemId)
        {
            Trace.Add("failed");
            return true;
        }
        public void HandleStorageDependencyFailed(QueueItem item, string message) =>
            Trace.Add("storage-failed");
        public string FormatException(Exception exception) => exception.Message;
        public void Log(string message) => Logs.Add(message);
    }
}
