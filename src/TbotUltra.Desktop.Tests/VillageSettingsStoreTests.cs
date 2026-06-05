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

        Assert.True(store.IsEnabledByKey("did:1", defaultIfUnknown: false));   // stored: capital on
        Assert.False(store.IsEnabledByKey("did:2", defaultIfUnknown: true));   // stored: non-capital off
        Assert.True(store.IsEnabledByKey("did:999", defaultIfUnknown: true));  // unknown → default
        Assert.True(store.IsEnabledByKey(null, defaultIfUnknown: true));       // no key → default
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
