using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousAutomationForecastCoordinatorTests
{
    [Fact]
    public void Resolve_UsesQueueDeadlineAndTheSamePreviewSelectionSeam()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var item = new QueueItem
        {
            TaskName = "hero_manage",
            Group = QueueGroup.Hero,
            Status = QueueStatus.Pending,
            NextAttemptAt = now.AddMinutes(3),
        };
        var port = new InMemoryPort(item);

        var forecast = new ContinuousAutomationForecastCoordinator(port).Resolve(now);

        Assert.Equal(ContinuousLoopForecastState.Waiting, forecast.State);
        Assert.Same(item, forecast.Item);
        Assert.Equal(item.NextAttemptAt, forecast.ReadyAtUtc);
    }

    [Fact]
    public void Resolve_SmartSleepQueueClearReplacesIntermediateConstructionDeadline()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var item = new QueueItem
        {
            TaskName = "upgrade_building_to_level",
            Group = QueueGroup.Construction,
            Status = QueueStatus.Pending,
            NextAttemptAt = now.AddMinutes(30),
        };
        var port = new InMemoryPort(item)
        {
            SmartSleepQueueClearDelay = TimeSpan.FromHours(2),
        };

        var forecast = new ContinuousAutomationForecastCoordinator(port).Resolve(
            now,
            wakeWhenConstructionQueueClears: true);

        Assert.Equal(ContinuousLoopForecastState.Waiting, forecast.State);
        Assert.Same(item, forecast.Item);
        Assert.Equal(now.AddHours(2), forecast.ReadyAtUtc);
    }

    private sealed class InMemoryPort(QueueItem item) : IContinuousAutomationForecastPort
    {
        public TimeSpan? SmartSleepQueueClearDelay { get; init; }
        public IReadOnlyList<QueueItem> GetQueueItems() => [item];
        public string? GetVillageKey(QueueItem candidate) => null;
        public bool IsAllowedByAutomationSettings(QueueItem candidate) => true;
        public TimeSpan? ResolveConstructionQueueDelay(QueueItem candidate, DateTimeOffset now) => null;
        public TimeSpan? ResolveSmartSleepConstructionQueueClearDelay(QueueItem candidate, DateTimeOffset now) =>
            SmartSleepQueueClearDelay;
        public TimeSpan? ResolveConstructPrerequisiteDelay(QueueItem candidate, DateTimeOffset now) => null;
        public QueueItem? SelectPreview(
            DateTimeOffset evaluationTime,
            string? villageKeyFilter,
            IReadOnlyList<QueueItem> queueItems) =>
            evaluationTime >= item.NextAttemptAt ? item : null;
        public bool HasKnownConstructionAvailability(QueueItem candidate, DateTimeOffset now) => true;
    }
}
