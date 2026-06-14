using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;
using Info = TbotUltra.Desktop.Services.VillageSettingsStore.VillageKeyInfo;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageSettingsStoreTests : IDisposable
{
    private readonly string _root;
    private string _activeAccount = "alice";

    public VillageSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-village-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Merge_NewVillages_CapitalEnabledOthersDisabled()
    {
        var store = CreateStore();
        var capital = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var second = new Info("did:2", "Second", 5, -3, IsCapital: false);

        store.Merge(new[] { capital, second });

        Assert.True(store.GetEnabled(capital));
        Assert.False(store.GetEnabled(second));
    }

    [Fact]
    public void GetEnabled_UnknownVillage_DefaultsToCapitalOnly()
    {
        var store = CreateStore();

        Assert.True(store.GetEnabled(new Info("did:9", "New capital", null, null, IsCapital: true)));
        Assert.False(store.GetEnabled(new Info("did:10", "New village", null, null, IsCapital: false)));
    }

    [Fact]
    public void SetEnabled_PersistsAcrossReload()
    {
        var store = CreateStore();
        var village = new Info("did:2", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { village });

        store.SetEnabled(village, enabled: true);

        var reloaded = CreateStore();
        Assert.True(reloaded.GetEnabled(village));
    }

    [Fact]
    public void Merge_KeepsUserChoice_WhenVillageRenamed()
    {
        var store = CreateStore();
        var village = new Info("did:2", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { village });
        store.SetEnabled(village, enabled: true);

        // Same stable id (did:2), new name: the enabled choice must survive.
        var renamed = new Info("did:2", "Renamed village", 5, -3, IsCapital: false);
        store.Merge(new[] { renamed });

        Assert.True(store.GetEnabled(renamed));
    }

    [Fact]
    public void Merge_DoesNotRemoveVillagesAbsentFromPartialRead()
    {
        var store = CreateStore();
        var a = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var b = new Info("did:2", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { a, b });
        store.SetEnabled(b, enabled: true);

        // A later partial page read only sees the capital; b must keep its choice.
        store.Merge(new[] { a });

        Assert.True(store.GetEnabled(b));
    }

    [Fact]
    public void Choices_AreScopedPerAccount()
    {
        var store = CreateStore();
        var village = new Info("did:2", "Second", 5, -3, IsCapital: false);

        _activeAccount = "alice";
        store.Merge(new[] { village });
        store.SetEnabled(village, enabled: true);

        _activeAccount = "bob";
        store.InvalidateCache();

        // Bob has never enabled this village, so it falls back to the default (non-capital = off).
        Assert.False(store.GetEnabled(village));
    }

    [Fact]
    public void IsEnabledByKey_ReturnsStoredState_AndDefaultsForUnknown()
    {
        var store = CreateStore();
        var capital = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var second = new Info("did:2", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { capital, second });

        // Villages are keyed by coordinates, so look them up by their canonical "xy:" key.
        Assert.True(store.IsEnabledByKey("xy:0|0", defaultIfUnknown: false));   // stored: capital on
        Assert.False(store.IsEnabledByKey("xy:5|-3", defaultIfUnknown: true));  // stored: non-capital off
        Assert.True(store.IsEnabledByKey("xy:9|9", defaultIfUnknown: true));    // unknown → default
        Assert.True(store.IsEnabledByKey(null, defaultIfUnknown: true));        // no key → default

        // A name-based key (as carried by queue items) resolves to the coordinate record.
        Assert.True(store.IsEnabledByKey("name:capital", defaultIfUnknown: false));
        Assert.Equal("xy:0|0", store.ResolveCanonicalKey("name:capital"));
    }

    [Fact]
    public void Reload_MergesDuplicateNewdidRecords_ByCoordinates()
    {
        // Legacy file: the same village ("SLAV") stored under two newdids with divergent group settings.
        var path = AccountStoragePaths.VillageSettingsPath(_root, _activeAccount);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "villages": [
            { "key": "did:106838", "name": "SLAV", "coordX": -29, "coordY": -66, "isCapital": true, "isEnabled": true, "enabledGroups": [], "lastSeenUtc": "2026-06-14T07:51:00+00:00" },
            { "key": "did:25471", "name": "SLAV", "coordX": -29, "coordY": -66, "isCapital": true, "isEnabled": true, "enabledGroups": ["troops"], "lastSeenUtc": "2026-06-14T07:51:32+00:00" }
          ]
        }
        """);

        var store = CreateStore();

        // Collapsed onto one coordinate key; the most recently seen record (with an explicit override) wins.
        var groups = store.GetEnabledGroups("xy:-29|-66");
        Assert.NotNull(groups);
        Assert.Contains("troops", groups!);

        // Both the coordinate key and the name-based key (queue items) resolve to the same record.
        Assert.Equal("xy:-29|-66", store.ResolveCanonicalKey("name:slav"));
        Assert.NotNull(store.GetEnabledGroups("name:slav"));
        Assert.Contains("troops", store.GetEnabledGroups("name:slav")!);
    }

    [Fact]
    public void VillagesFile_WrittenUnderActiveAccount()
    {
        var store = CreateStore();
        var village = new Info("did:1", "Capital", 0, 0, IsCapital: true);

        store.Merge(new[] { village });

        Assert.True(File.Exists(AccountStoragePaths.VillageSettingsPath(_root, "alice")));
    }

    private VillageSettingsStore CreateStore()
    {
        return new VillageSettingsStore(_root, () => _activeAccount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
