using TbotUltra.Core.Farming;
using TbotUltra.Core.Accounts;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListHistoryStoreTests
{
    [Fact]
    public void AppendAndClear_AreScopedToAccountAndServerAndTrackPreviousSend()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-farmlist-history-tests", Guid.NewGuid().ToString("N"));
        var first = DateTimeOffset.UtcNow.AddMinutes(-20);
        var serverA = "https://a.travian.com";
        var serverB = "https://b.travian.com";

        FarmListHistoryStore.Append(root, "alice", serverA,
            [new(first, "Raiders", "42", "Village", "Manual", "success", null)]);
        FarmListHistoryStore.Append(root, "alice", serverA,
            [new(first.AddMinutes(10), "Raiders", "42", "Village", "Automatic", "error", null)]);
        FarmListHistoryStore.Append(root, "alice", serverB,
            [new(first, "Other", "51", "Village", "Manual", "success", null)]);

        var history = FarmListHistoryStore.Load(root, "alice", serverA);
        Assert.Equal(2, history.Count);
        Assert.Null(history[0].SincePrevious);
        Assert.Equal(TimeSpan.FromMinutes(10), history[1].SincePrevious);
        Assert.Equal("error", history[1].Response);
        Assert.Empty(FarmListHistoryStore.Load(root, "bob", serverA));

        FarmListHistoryStore.Clear(root, "alice", serverA);
        Assert.Empty(FarmListHistoryStore.Load(root, "alice", serverA));
        Assert.Single(FarmListHistoryStore.Load(root, "alice", serverB));
    }

    [Fact]
    public void Append_DropsEntriesOlderThanThirtyDays()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-farmlist-history-tests", Guid.NewGuid().ToString("N"));
        FarmListHistoryStore.Append(root, "alice", "https://a.travian.com",
            [new(DateTimeOffset.UtcNow.AddDays(-31), "Old", "1", null, "Manual", "success", null)]);
        Assert.Empty(FarmListHistoryStore.Load(root, "alice", "https://a.travian.com"));
    }

    [Fact]
    public void Append_QuarantinesCorruptHistoryAndContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-farmlist-history-tests", Guid.NewGuid().ToString("N"));
        var server = "https://a.travian.com";
        var path = AccountStoragePaths.FarmListHistoryPath(root, "alice", server);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{broken");
        var logs = new List<string>();

        FarmListHistoryStore.Append(root, "alice", server,
            [new(DateTimeOffset.UtcNow, "Raiders", "42", "Village", "Manual", "success", null)], logs.Add);

        Assert.Single(FarmListHistoryStore.Load(root, "alice", server));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
        Assert.Contains(logs, message => message.Contains("corrupt send history"));
    }
}
