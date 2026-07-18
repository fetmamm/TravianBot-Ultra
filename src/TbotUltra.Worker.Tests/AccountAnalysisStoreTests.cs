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
    public void Save_ThenLoad_PreservesStableVillageSnapshot()
    {
        var snapshot = new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "main",
            ServerUrl: "https://example.com",
            Tribe: "Gauls",
            GoldClubEnabled: true,
            BuildingCatalog: [],
            Villages:
            [
                new Village("Capital", "dorf1.php?newdid=42", true, 11, -7, 321, Tribe: "Gauls"),
            ]);

        _store.Save(snapshot);

        Assert.True(_store.TryLoad("main", out var loaded, "https://example.com"));
        var village = Assert.Single(loaded!.Villages!);
        Assert.Equal("Capital", village.Name);
        Assert.Equal("dorf1.php?newdid=42", village.Url);
        Assert.Equal((11, -7), (village.CoordX, village.CoordY));
        Assert.Equal(321, village.Population);
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

    [Fact]
    public void Save_SerializesConcurrentWritersAndLeavesValidJson()
    {
        Parallel.For(0, 20, index =>
        {
            _store.Save(new AccountAnalysisSnapshot(
                SchemaVersion: 1,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: "shared",
                ServerUrl: "https://example.com",
                Tribe: index % 2 == 0 ? "Romans" : "Gauls",
                GoldClubEnabled: true,
                BuildingCatalog: []));
        });

        Assert.True(_store.TryLoad("shared", out var result, "https://example.com"));
        Assert.NotNull(result);
        Assert.Contains(result!.Tribe, new[] { "Romans", "Gauls" });
        var directory = Path.GetDirectoryName(_store.GetFilePath("shared", "https://example.com"))!;
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public void SaveWorldUid_PreservesExistingAnalysis()
    {
        var snapshot = new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: "main",
            ServerUrl: "https://ts50.x5.arabics.travian.com",
            Tribe: "Romans",
            GoldClubEnabled: true,
            BuildingCatalog: [],
            AutoCelebrationEnabled: true);
        _store.Save(snapshot);

        var worldUid = Guid.NewGuid().ToString();
        _store.SaveWorldUid(snapshot.AccountName, snapshot.ServerUrl, worldUid);

        Assert.True(_store.TryLoad(snapshot.AccountName, out var loaded, snapshot.ServerUrl));
        Assert.Equal(worldUid, loaded!.WorldUid);
        Assert.Equal("Romans", loaded.Tribe);
        Assert.True(loaded.GoldClubEnabled);
        Assert.True(loaded.AutoCelebrationEnabled);
    }

    [Fact]
    public void SaveWorldUid_WithoutAnalysis_DoesNotMarkAccountAnalyzed()
    {
        var serverUrl = "https://ts50.x5.arabics.travian.com";
        _store.SaveWorldUid("new-account", serverUrl, Guid.NewGuid().ToString());

        Assert.True(_store.TryLoad("new-account", out var loaded, serverUrl));
        Assert.Equal(0, loaded!.SchemaVersion);
        Assert.False(_store.IsAnalyzed("new-account", serverUrl));
    }

    [Fact]
    public void ConcurrentFieldUpdates_DoNotLoseWorldUidOrAnalysisFields()
    {
        const string accountName = "race-account";
        const string serverUrl = "https://ts50.x5.arabics.travian.com";
        var worldUid = Guid.NewGuid().ToString();

        Parallel.For(0, 100, index =>
        {
            if (index % 2 == 0)
            {
                _store.SaveWorldUid(accountName, serverUrl, worldUid);
                return;
            }

            _store.Update(accountName, serverUrl, existing => new AccountAnalysisSnapshot(
                SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: accountName,
                ServerUrl: serverUrl,
                Tribe: "Romans",
                GoldClubEnabled: true,
                BuildingCatalog: existing?.BuildingCatalog ?? [],
                AutoCelebrationEnabled: true,
                WorldUid: existing?.WorldUid));
        });

        Assert.True(_store.TryLoad(accountName, out var loaded, serverUrl));
        Assert.Equal(worldUid, loaded!.WorldUid);
        Assert.Equal("Romans", loaded.Tribe);
        Assert.True(loaded.AutoCelebrationEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
