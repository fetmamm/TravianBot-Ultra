using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class AccountAnalysisStoreTests : IDisposable
{
    private readonly string _root;
    private readonly AccountAnalysisStore _store;

    public AccountAnalysisStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new AccountAnalysisStore(_root);
    }

    [Fact]
    public void Save_ThenLoad_WorksAndMarksAnalyzed()
    {
        var snapshot = new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "main",
            ServerUrl: "https://example.com",
            Tribe: "Romans",
            GoldClubEnabled: true,
            BuildingCatalog: []);

        _store.Save(snapshot);

        var loaded = _store.TryLoad("main", out var result);
        Assert.True(loaded);
        Assert.NotNull(result);
        Assert.Equal("Romans", result!.Tribe);
        Assert.True(_store.IsAnalyzed("main"));
    }

    [Fact]
    public void IsAnalyzed_ReturnsFalse_OnSchemaMismatch()
    {
        var snapshot = new AccountAnalysisSnapshot(
            SchemaVersion: 999,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "legacy",
            ServerUrl: "https://example.com",
            Tribe: "Gauls",
            GoldClubEnabled: false,
            BuildingCatalog: []);
        _store.Save(snapshot);

        Assert.False(_store.IsAnalyzed("legacy"));
    }

    [Fact]
    public void TryLoad_ReturnsFalse_OnCorruptJson()
    {
        var path = _store.GetFilePath("broken");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ invalid json");

        var loaded = _store.TryLoad("broken", out var analysis);
        Assert.False(loaded);
        Assert.Null(analysis);
        Assert.False(_store.IsAnalyzed("broken"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
