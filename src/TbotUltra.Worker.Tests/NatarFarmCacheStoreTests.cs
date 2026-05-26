using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class NatarFarmCacheStoreTests : IDisposable
{
    private readonly string _root;
    private readonly NatarFarmCacheStore _store;

    public NatarFarmCacheStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-natar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new NatarFarmCacheStore(_root);
    }

    [Fact]
    public void Save_WritesPerAccountCachePath()
    {
        var snapshot = new NatarFarmCacheSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "alice",
            ServerUrl: "https://example.com",
            SelectionMode: "farm_villages",
            Coordinates: [new NatarFarmCoordinate(1, 2, "Natar")]);

        _store.Save(snapshot);

        Assert.True(File.Exists(AccountStoragePaths.NatarFarmCachePath(_root, "alice", "https://example.com", "farm_villages")));
    }

    [Fact]
    public void TryLoad_MigratesLegacyCache_ToPerAccountPath()
    {
        var snapshot = new NatarFarmCacheSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "legacy",
            ServerUrl: "https://example.com",
            SelectionMode: "all_villages",
            Coordinates: [new NatarFarmCoordinate(3, 4, "Natar")]);
        var legacyPath = AccountStoragePaths.LegacyNatarFarmCachePath(_root, "legacy", "https://example.com", "all_villages");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, System.Text.Json.JsonSerializer.Serialize(snapshot));

        var loaded = _store.TryLoad("legacy", out var result, "https://example.com", "all_villages");

        Assert.True(loaded);
        Assert.NotNull(result);
        Assert.Single(result!.Coordinates);
        Assert.True(File.Exists(AccountStoragePaths.NatarFarmCachePath(_root, "legacy", "https://example.com", "all_villages")));
        Assert.False(File.Exists(legacyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
