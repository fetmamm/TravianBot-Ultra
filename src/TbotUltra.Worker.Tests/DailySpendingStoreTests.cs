using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class DailySpendingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-daily-spending-{Guid.NewGuid():N}");

    [Fact]
    public void TryReserveGold_PersistsAndEnforcesDailyLimit()
    {
        var path = Path.Combine(_root, "state.json");
        var date = new DateOnly(2026, 7, 19);

        Assert.True(new DailySpendingStore(path).TryReserveGold(date, 6, 3, out var firstSpent));
        Assert.True(new DailySpendingStore(path).TryReserveGold(date, 6, 3, out var secondSpent));
        Assert.False(new DailySpendingStore(path).TryReserveGold(date, 6, 3, out var rejectedSpent));

        Assert.Equal(3, firstSpent);
        Assert.Equal(6, secondSpent);
        Assert.Equal(6, rejectedSpent);
    }

    [Fact]
    public void TryReserveGold_NewServerDateResetsSpentAmount()
    {
        var path = Path.Combine(_root, "state.json");
        var store = new DailySpendingStore(path);

        Assert.True(store.TryReserveGold(new DateOnly(2026, 7, 19), 3, 3, out _));
        Assert.True(store.TryReserveGold(new DateOnly(2026, 7, 20), 3, 3, out var spent));

        Assert.Equal(3, spent);
    }

    [Fact]
    public void ResetGold_OnlyClearsGoldCounter()
    {
        var path = Path.Combine(_root, "state.json");
        var date = new DateOnly(2026, 7, 19);
        var store = new DailySpendingStore(path);
        Assert.True(store.TryReserveGold(date, 20, 3, out _));

        store.ResetGold();

        var state = store.Read(date);
        Assert.Equal(0, state.GoldSpent);
        Assert.Equal(0, state.SilverSpent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
