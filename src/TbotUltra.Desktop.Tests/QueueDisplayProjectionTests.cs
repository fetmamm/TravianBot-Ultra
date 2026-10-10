using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueueDisplayProjectionTests
{
    [Fact]
    public void Build_EstimatesOnlyActiveItemsAndKeepsCompleteHistory()
    {
        var pending = Item(QueueStatus.Pending, runtimeOnly: false);
        var completed = Enumerable.Range(0, 1_500)
            .Select(_ => Item(QueueStatus.Succeeded, runtimeOnly: true))
            .ToList();
        var estimateCalls = 0;

        var projection = QueueDisplayProjection.Build(
            [pending, .. completed],
            item =>
            {
                estimateCalls++;
                return Row(item);
            });

        Assert.Equal(1, estimateCalls);
        Assert.Single(projection.ActiveRows);
        Assert.Equal(1_500, projection.HistoryItems.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_MovesEveryFailedItemToHistory(bool runtimeOnly)
    {
        var failed = Item(QueueStatus.Failed, runtimeOnly);

        var projection = QueueDisplayProjection.Build([failed], Row);

        Assert.Empty(projection.ActiveRows);
        Assert.Same(failed, Assert.Single(projection.HistoryItems));
    }

    [Fact]
    public void Build_HistoryContainsOnlyConstructionItems()
    {
        var construction = Item(QueueStatus.Succeeded, runtimeOnly: false);
        construction.Group = QueueGroup.Construction;
        var account = Item(QueueStatus.Succeeded, runtimeOnly: true);
        account.Group = QueueGroup.Account;
        var farming = Item(QueueStatus.Failed, runtimeOnly: true);
        farming.Group = QueueGroup.Farming;

        var projection = QueueDisplayProjection.Build([construction, account, farming], Row);

        Assert.Same(construction, Assert.Single(projection.HistoryItems));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Build_RepresentativeQueueProjectionStaysBelowTheUiBudget()
    {
        var items = Enumerable.Range(0, 100)
            .Select(_ => Item(QueueStatus.Pending, runtimeOnly: false))
            .Concat(Enumerable.Range(0, 1_500)
                .Select(_ => Item(QueueStatus.Succeeded, runtimeOnly: true)))
            .ToList();
        _ = QueueDisplayProjection.Build(items, Row);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var projection = QueueDisplayProjection.Build(items, Row);
        stopwatch.Stop();

        Assert.Equal(100, projection.ActiveRows.Count);
        Assert.Equal(1_500, projection.HistoryItems.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Queue projection exceeded the 100 ms UI budget: {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static QueueItem Item(QueueStatus status, bool runtimeOnly) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = "test",
        Status = status,
        IsRuntimeOnly = runtimeOnly,
    };

    private static QueueItemRow Row(QueueItem item) => new()
    {
        Id = item.Id,
        Status = item.Status,
        IsRuntimeOnly = item.IsRuntimeOnly,
    };
}
