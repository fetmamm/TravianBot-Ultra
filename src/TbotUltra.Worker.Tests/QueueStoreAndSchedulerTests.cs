using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class QueueStoreAndSchedulerTests : IDisposable
{
    private readonly string _root;
    private readonly string _queuePath;

    public QueueStoreAndSchedulerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _queuePath = Path.Combine(_root, "queue.json");
    }

    [Fact]
    public void Scheduler_UsesPriorityAndFifo()
    {
        var store = new JsonQueueStore(_queuePath);
        var scheduler = new PriorityFifoQueueScheduler();

        var low = store.Add("status", null, priority: 1, maxRetries: 3);
        Thread.Sleep(20);
        var highA = store.Add("scan_all_villages", null, priority: 5, maxRetries: 3);
        Thread.Sleep(20);
        var highB = store.Add("account_snapshot", null, priority: 5, maxRetries: 3);

        var ordered = scheduler.OrderForDisplay(store.GetAll()).ToList();
        Assert.Equal(highA.Id, ordered[0].Id);
        Assert.Equal(highB.Id, ordered[1].Id);
        Assert.Equal(low.Id, ordered[2].Id);

        var next = scheduler.SelectNext(store.GetAll());
        Assert.NotNull(next);
        Assert.Equal(highA.Id, next!.Id);
    }

    [Fact]
    public void PauseAndResume_ChangesEligibility()
    {
        var store = new JsonQueueStore(_queuePath);
        var scheduler = new PriorityFifoQueueScheduler();
        var item = store.Add("status", null, priority: 1, maxRetries: 3);

        Assert.True(store.Pause(item.Id));
        var paused = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(QueueStatus.Paused, paused.Status);
        Assert.Null(scheduler.SelectNext(store.GetAll()));

        Assert.True(store.Resume(item.Id));
        var resumed = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(QueueStatus.Pending, resumed.Status);
        Assert.NotNull(scheduler.SelectNext(store.GetAll()));
    }

    [Fact]
    public void MarkExecutionFailed_RetriesAndThenFails()
    {
        var store = new JsonQueueStore(_queuePath);
        var item = store.Add("status", null, priority: 1, maxRetries: 1);

        Assert.True(store.MarkRunning(item.Id));
        Assert.True(store.MarkExecutionFailed(item.Id));
        var firstFail = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(1, firstFail.Retries);
        Assert.Equal(QueueStatus.Pending, firstFail.Status);

        Assert.True(store.MarkRunning(item.Id));
        Assert.True(store.MarkExecutionFailed(item.Id));
        var secondFail = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(2, secondFail.Retries);
        Assert.Equal(QueueStatus.Failed, secondFail.Status);
    }

    [Fact]
    public void TaskCatalog_AllowsKnownTasks_AndRejectsUnknown()
    {
        Assert.True(TaskCatalog.IsAllowed("status"));
        Assert.True(TaskCatalog.IsAllowed("upgrade_building_to_max"));
        Assert.True(TaskCatalog.IsAllowed("demolish_building_to_level"));
        Assert.True(TaskCatalog.IsAllowed("hero_manage"));
        Assert.False(TaskCatalog.IsAllowed("train_troops"));
    }

    [Fact]
    public void QueueStore_HandlesHundredsOfOperations_WithoutLosingItems()
    {
        var store = new JsonQueueStore(_queuePath);

        for (var i = 0; i < 150; i++)
        {
            store.Add("status", null, priority: i % 3, maxRetries: 3);
        }

        var items = store.GetAll();
        Assert.Equal(150, items.Count);

        foreach (var item in items.Take(100))
        {
            Assert.True(store.MarkRunning(item.Id));
            Assert.True(store.MarkSucceeded(item.Id));
        }

        var succeededCount = store.GetAll().Count(item => item.Status == QueueStatus.Succeeded);
        Assert.Equal(100, succeededCount);
    }

    [Fact]
    public void QueueStore_RemovesStaleTempFileBeforeSave()
    {
        File.WriteAllText($"{_queuePath}.tmp", "stale");
        var store = new JsonQueueStore(_queuePath);

        store.Add("status", null, priority: 1, maxRetries: 3);

        Assert.False(File.Exists($"{_queuePath}.tmp"));
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void BotTaskRunner_RegistersHandlers_ForEveryAllowedTask()
    {
        var allowed = TaskCatalog.AllowedTaskNames;
        var registered = BotTaskRunner.RegisteredTaskNames;
        foreach (var task in allowed)
        {
            Assert.Contains(task, registered, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Resource slot 1 blocked (BlockedByQueue): workers busy.", true)]
    [InlineData("Building cannot be built yet. Missing requirements.", true)]
    [InlineData("Slot 20 reports max level reached.", true)]
    [InlineData("Slot 20: already at level 3.", false)]
    [InlineData("", false)]
    public void BotTaskRunner_IsBlockedTaskResult_MatchesKnownFormats(string result, bool expected)
    {
        Assert.Equal(expected, BotTaskRunner.IsBlockedTaskResult(result));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
