using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BulkMessageSentCacheStoreTests : IDisposable
{
    private readonly string _root;
    private readonly BulkMessageSentCacheStore _store;

    public BulkMessageSentCacheStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-bulk-message-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new BulkMessageSentCacheStore(_root);
    }

    [Fact]
    public void AddSentPlayers_WritesPerAccountPerServerCache()
    {
        _store.AddSentPlayers("alice", "https://ts50.x5.asia.travian.com", ["Bob"], DateTimeOffset.UtcNow);

        var path = AccountStoragePaths.BulkMessageSentCachePath(_root, "alice", "https://ts50.x5.asia.travian.com");
        Assert.True(File.Exists(path));
        Assert.Equal(["Bob"], _store.LoadSentPlayerNames("alice", "https://ts50.x5.asia.travian.com"));
        Assert.Empty(_store.LoadSentPlayerNames("alice", "https://other.example.com"));
    }

    [Fact]
    public void AddSentPlayers_DeduplicatesCaseInsensitiveNames()
    {
        _store.AddSentPlayers("alice", "https://example.com", ["Bob", "bob"], DateTimeOffset.UtcNow);

        var names = _store.LoadSentPlayerNames("alice", "https://example.com");

        Assert.Single(names);
        Assert.Equal("bob", names[0]);
    }

    [Fact]
    public void Clear_RemovesOnlySelectedServerCache()
    {
        _store.AddSentPlayers("alice", "https://one.example.com", ["One"], DateTimeOffset.UtcNow);
        _store.AddSentPlayers("alice", "https://two.example.com", ["Two"], DateTimeOffset.UtcNow);

        _store.Clear("alice", "https://one.example.com");

        Assert.Empty(_store.LoadSentPlayerNames("alice", "https://one.example.com"));
        Assert.Equal(["Two"], _store.LoadSentPlayerNames("alice", "https://two.example.com"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
