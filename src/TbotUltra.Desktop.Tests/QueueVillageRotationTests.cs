using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueueVillageRotationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // Village is carried in the payload; the test helper keys items by a "village" payload entry.
    private static string? VillageKey(QueueItem item)
        => item.Payload.TryGetValue("village", out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Default test predicate: every village enabled.
    private static bool AllEnabled(QueueItem item) => true;

    private static QueueItem Item(string village, QueueStatus status = QueueStatus.Pending, int secondsUntilReady = 0)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "upgrade_building_to_level",
            Status = status,
            NextAttemptAt = Now.AddSeconds(secondsUntilReady),
            Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["village"] = village },
        };
    }

    [Fact]
    public void SelectNext_DrainsOneVillageBeforeRotating()
    {
        var a1 = Item("A");
        var a2 = Item("A");
        var b1 = Item("B");
        var items = new[] { a1, a2, b1 };
        string? rotation = null;

        // First pick establishes village A and returns A's first ready item.
        var first = QueueVillageRotation.SelectNext(items, Now, VillageKey, AllEnabled, ref rotation);
        Assert.Equal(a1.Id, first!.Id);
        Assert.Equal("A", rotation);

        // While A still has ready work, stay on A even though B is also ready.
        var remaining = new[] { a2, b1 };
        var second = QueueVillageRotation.SelectNext(remaining, Now, VillageKey, AllEnabled, ref rotation);
        Assert.Equal(a2.Id, second!.Id);
        Assert.Equal("A", rotation);

        // A is now drained → rotate to B.
        var third = QueueVillageRotation.SelectNext(new[] { b1 }, Now, VillageKey, AllEnabled, ref rotation);
        Assert.Equal(b1.Id, third!.Id);
        Assert.Equal("B", rotation);
    }

    [Fact]
    public void SelectNext_SkipsCurrentVillage_WhenItsTasksAreAllDeferred()
    {
        // A is the current rotation village but its only task is deferred (waiting for resources);
        // rotation must advance to B, which has a ready task.
        var aDeferred = Item("A", secondsUntilReady: 600);
        var bReady = Item("B");
        string? rotation = "A";

        var pick = QueueVillageRotation.SelectNext(new[] { aDeferred, bReady }, Now, VillageKey, AllEnabled, ref rotation);

        Assert.Equal(bReady.Id, pick!.Id);
        Assert.Equal("B", rotation);
    }

    [Fact]
    public void SelectNext_ReturnsNull_WhenNothingReady()
    {
        var deferred = Item("A", secondsUntilReady: 300);
        var paused = Item("B", status: QueueStatus.Paused);
        string? rotation = null;

        var pick = QueueVillageRotation.SelectNext(new[] { deferred, paused }, Now, VillageKey, AllEnabled, ref rotation);

        Assert.Null(pick);
        Assert.Null(rotation); // rotation is not advanced when nothing is selected
    }

    [Fact]
    public void SelectNext_SkipsItemsForDisabledVillages()
    {
        var a = Item("A");
        var b = Item("B");
        string? rotation = null;

        // Only village B is enabled → A's item is skipped even though it is first in display order.
        bool OnlyBEnabled(QueueItem item) => VillageKey(item) == "B";
        var pick = QueueVillageRotation.SelectNext(new[] { a, b }, Now, VillageKey, OnlyBEnabled, ref rotation);

        Assert.Equal(b.Id, pick!.Id);
        Assert.Equal("B", rotation);
    }

    // Simple per-village selector for SelectByVillageRotation tests: first ready (due, Pending) item.
    private static QueueItem? FirstReady(IReadOnlyList<QueueItem> items)
        => items.FirstOrDefault(i => i.Status == QueueStatus.Pending && i.NextAttemptAt <= Now);

    [Fact]
    public void SelectByVillageRotation_DrainsVillageThenRotates()
    {
        var a1 = Item("A");
        var a2 = Item("A");
        var b1 = Item("B");
        string? rotation = null;

        var first = QueueVillageRotation.SelectByVillageRotation(new[] { a1, a2, b1 }, VillageKey, FirstReady, ref rotation);
        Assert.Equal(a1.Id, first!.Id);
        Assert.Equal("A", rotation);

        var second = QueueVillageRotation.SelectByVillageRotation(new[] { a2, b1 }, VillageKey, FirstReady, ref rotation);
        Assert.Equal(a2.Id, second!.Id);
        Assert.Equal("A", rotation);

        var third = QueueVillageRotation.SelectByVillageRotation(new[] { b1 }, VillageKey, FirstReady, ref rotation);
        Assert.Equal(b1.Id, third!.Id);
        Assert.Equal("B", rotation);
    }

    [Fact]
    public void SelectByVillageRotation_RotatesWhenCurrentVillageHasNoSelectableItem()
    {
        // Village A is current but its only item is deferred → the per-village selector yields nothing,
        // so rotation advances to B which has a ready item.
        var aDeferred = Item("A", secondsUntilReady: 600);
        var bReady = Item("B");
        string? rotation = "A";

        var pick = QueueVillageRotation.SelectByVillageRotation(new[] { aDeferred, bReady }, VillageKey, FirstReady, ref rotation);

        Assert.Equal(bReady.Id, pick!.Id);
        Assert.Equal("B", rotation);
    }

    [Fact]
    public void SelectByVillageRotation_ReturnsNull_WhenNoVillageHasSelectableItem()
    {
        var aDeferred = Item("A", secondsUntilReady: 600);
        var bDeferred = Item("B", secondsUntilReady: 600);
        string? rotation = null;

        var pick = QueueVillageRotation.SelectByVillageRotation(new[] { aDeferred, bDeferred }, VillageKey, FirstReady, ref rotation);

        Assert.Null(pick);
    }

    [Fact]
    public void SelectNext_TreatsItemsWithoutVillage_AsOneDefaultGroup()
    {
        var d1 = new QueueItem { Id = Guid.NewGuid(), TaskName = "status", Status = QueueStatus.Pending, NextAttemptAt = Now };
        var d2 = new QueueItem { Id = Guid.NewGuid(), TaskName = "status", Status = QueueStatus.Pending, NextAttemptAt = Now };
        string? rotation = null;

        var first = QueueVillageRotation.SelectNext(new[] { d1, d2 }, Now, VillageKey, AllEnabled, ref rotation);
        Assert.Equal(d1.Id, first!.Id);

        var second = QueueVillageRotation.SelectNext(new[] { d2 }, Now, VillageKey, AllEnabled, ref rotation);
        Assert.Equal(d2.Id, second!.Id);
    }
}
