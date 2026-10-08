using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class WatchtowerSnapshotStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "TbotUltra.WatchtowerSnapshotStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_PreservesVillageStatusAcrossStoreInstances()
    {
        var observedAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var expected = new WatchtowerStatus(19, [], observedAt);
        new WatchtowerSnapshotStore(_rootPath).Save(
            "account-one",
            "https://ts100.example.com",
            "xy:164|110",
            expected);

        var restartedStore = new WatchtowerSnapshotStore(_rootPath);

        Assert.True(restartedStore.TryLoad(
            "account-one",
            "https://ts100.example.com",
            out var statuses));
        var actual = statuses["xy:164|110"];
        Assert.Equal(expected.Level, actual.Level);
        Assert.Equal(expected.ObservedAtUtc, actual.ObservedAtUtc);
        Assert.Empty(actual.Active);
    }

    [Fact]
    public void TryLoad_DoesNotReturnAnotherAccountOrServerStatus()
    {
        var store = new WatchtowerSnapshotStore(_rootPath);
        store.Save(
            "account-one",
            "https://ts100.example.com",
            "xy:164|110",
            new WatchtowerStatus(19, [], DateTimeOffset.UtcNow));

        Assert.False(store.TryLoad("account-two", "https://ts100.example.com", out _));
        Assert.False(store.TryLoad("account-one", "https://ts200.example.com", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
