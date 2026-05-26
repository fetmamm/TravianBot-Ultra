using TbotUltra.Worker.Domain;
using TbotUltra.Core.Accounts;
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

        var loaded = _store.TryLoad("main", out var result, "https://example.com");
        Assert.True(loaded);
        Assert.NotNull(result);
        Assert.Equal("Romans", result!.Tribe);
        Assert.True(_store.IsAnalyzed("main", "https://example.com"));
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

        Assert.False(_store.IsAnalyzed("legacy", "https://example.com"));
    }

    [Fact]
    public void Save_ThenLoad_PreservesAutomationLoopPreferences()
    {
        var snapshot = new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "main",
            ServerUrl: "https://example.com",
            Tribe: "Romans",
            GoldClubEnabled: true,
            BuildingCatalog: [],
            AutoCelebrationEnabled: true,
            AutomationLoopEnabledGroups: ["hero", "farming"],
            AutomationLoopVisibleGroups: ["hero"]);

        _store.Save(snapshot);

        var loaded = _store.TryLoad("main", out var result, "https://example.com");
        Assert.True(loaded);
        Assert.NotNull(result);
        Assert.Equal(["hero", "farming"], result!.AutomationLoopEnabledGroups);
        Assert.Equal(["hero"], result.AutomationLoopVisibleGroups);
        Assert.True(result.AutoCelebrationEnabled);
    }

    [Fact]
    public void TryLoad_ReturnsFalse_OnCorruptJson()
    {
        var path = _store.GetFilePath("broken");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ invalid json");

        var loaded = _store.TryLoad("broken", out var analysis, "https://example.com");
        Assert.False(loaded);
        Assert.Null(analysis);
        Assert.False(_store.IsAnalyzed("broken", "https://example.com"));
    }

    [Fact]
    public void TryLoad_MigratesLegacyFile_ToPerAccountPath()
    {
        var snapshot = new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "legacy",
            ServerUrl: "https://example.com",
            Tribe: "Teutons",
            GoldClubEnabled: true,
            BuildingCatalog: []);
        var legacyPath = AccountStoragePaths.LegacyAnalysisPath(_root, "legacy", "https://example.com");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, System.Text.Json.JsonSerializer.Serialize(snapshot));

        var loaded = _store.TryLoad("legacy", out var result, "https://example.com");

        Assert.True(loaded);
        Assert.NotNull(result);
        Assert.Equal("Teutons", result!.Tribe);
        Assert.True(File.Exists(AccountStoragePaths.AnalysisPath(_root, "legacy", "https://example.com")));
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
