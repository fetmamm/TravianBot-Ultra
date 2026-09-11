using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TaskActivityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tbot-ultra-task-activity-tests",
        Guid.NewGuid().ToString("N"));

    public TaskActivityStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Statistics_CountEveryRecordedExecutionInsteadOfQueueItems()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        TaskActivityStore.Record(_root, "alice", "upgrade_building_to_level", now.AddMinutes(-3));
        TaskActivityStore.Record(_root, "alice", "upgrade_building_to_level", now.AddMinutes(-2));
        TaskActivityStore.Record(_root, "alice", "upgrade_building_to_level", now.AddMinutes(-1));

        var row = Assert.Single(TaskActivityStatistics.Build(
            TaskActivityStore.Load(_root, "alice"),
            now.AddDays(-7)));

        Assert.Equal("upgrade_building_to_level", row.TaskName);
        Assert.Equal(3, row.Runs);
        Assert.Equal(now.AddMinutes(-1), row.LastRunUtc);
        Assert.Equal(now.AddMinutes(-1).ToLocalTime().Hour, row.PeakLocalHour);
    }

    [Fact]
    public void History_IsSeparatedByAccount()
    {
        var now = DateTimeOffset.UtcNow;
        TaskActivityStore.Record(_root, "alice", "collect_tasks", now);
        TaskActivityStore.Record(_root, "bob", "collect_daily_quests", now);

        Assert.Equal("collect_tasks", Assert.Single(TaskActivityStore.Load(_root, "alice")).TaskName);
        Assert.Equal("collect_daily_quests", Assert.Single(TaskActivityStore.Load(_root, "bob")).TaskName);
    }

    [Fact]
    public void CorruptHistory_ReturnsNoFabricatedEntries()
    {
        var path = AccountStoragePaths.TaskActivityHistoryPath(_root, "alice");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ invalid json");

        Assert.Empty(TaskActivityStore.Load(_root, "alice"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
