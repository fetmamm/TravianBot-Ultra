using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroInventorySnapshotStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "TbotUltra.HeroInventorySnapshotStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_PreservesInventoryAcrossStoreInstances()
    {
        var expected = new HeroInventoryResources(65001, 65002, 65003, 65004);
        new HeroInventorySnapshotStore(_rootPath).Save(
            "account-one",
            "https://ts100.example.com",
            expected);

        var restartedStore = new HeroInventorySnapshotStore(_rootPath);

        Assert.True(restartedStore.TryLoad(
            "account-one",
            "https://ts100.example.com",
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryLoad_DoesNotReturnAnotherAccountOrServerInventory()
    {
        var store = new HeroInventorySnapshotStore(_rootPath);
        store.Save(
            "account-one",
            "https://ts100.example.com",
            new HeroInventoryResources(1, 2, 3, 4));

        Assert.False(store.TryLoad("account-two", "https://ts100.example.com", out _));
        Assert.False(store.TryLoad("account-one", "https://ts200.example.com", out _));
    }

    [Fact]
    public void SaveAndLoadSnapshot_PreservesObservationAndProbeMetadata()
    {
        var observedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var expected = new HeroInventorySnapshot(
            new HeroInventoryResources(),
            observedAt,
            HeroInventoryObservationSource.EmptyToast,
            ConsecutiveEmptyObservations: 2,
            NextProbeAtUtc: observedAt.AddMinutes(37),
            ConstructionProbe: new HeroConstructionProbeState(3, observedAt.AddMinutes(52)));
        var store = new HeroInventorySnapshotStore(_rootPath);

        store.SaveSnapshot("account-one", "https://ts100.example.com", expected);

        Assert.True(store.TryLoadSnapshot(
            "account-one",
            "https://ts100.example.com",
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LegacyEmptySnapshot_LoadsAndIsImmediatelyEligibleForAProbe()
    {
        const string accountName = "account-one";
        const string serverUrl = "https://ts100.example.com";
        var filePath = AccountStoragePaths.HeroInventorySnapshotPath(_rootPath, accountName, serverUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(
            filePath,
            """
            {
              "accountName": "account-one",
              "serverUrl": "https://ts100.example.com",
              "resources": { "wood": 0, "clay": 0, "iron": 0, "crop": 0 },
              "updatedAtUtc": "2026-08-27T10:00:00+00:00"
            }
            """);
        var store = new HeroInventorySnapshotStore(_rootPath);

        Assert.True(store.TryLoadSnapshot(accountName, serverUrl, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.True(HeroInventoryProbePolicy.ShouldProbe(
            snapshot,
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
