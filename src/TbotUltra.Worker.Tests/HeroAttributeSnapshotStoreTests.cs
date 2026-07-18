using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroAttributeSnapshotStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "TbotUltra.HeroAttributeSnapshotStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_PreservesSnapshotForMatchingAccountAndServer()
    {
        var store = new HeroAttributeSnapshotStore(_rootPath);
        var expected = new HeroAttributeSnapshot(
            FreePoints: 2,
            FightingStrength: 16,
            OffenceBonus: 3,
            DefenceBonus: 4,
            Resources: 68,
            AdventureCount: 5,
            HeroState: "Home",
            HomeVillageName: "New village",
            HomeVillageCoordX: 93,
            HomeVillageCoordY: -19);

        store.Save("account-one", "https://ts100.example.com", expected);

        Assert.True(store.TryLoad(
            "account-one",
            "https://ts100.example.com",
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryLoad_DoesNotReturnAnotherAccountOrServerSnapshot()
    {
        var store = new HeroAttributeSnapshotStore(_rootPath);
        store.Save(
            "account-one",
            "https://ts100.example.com",
            new HeroAttributeSnapshot(FightingStrength: 16));

        Assert.False(store.TryLoad(
            "account-two",
            "https://ts100.example.com",
            out _));
        Assert.False(store.TryLoad(
            "account-one",
            "https://ts200.example.com",
            out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
