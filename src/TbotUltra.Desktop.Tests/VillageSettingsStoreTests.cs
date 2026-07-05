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
    public void Merge_OnlyInitialVillage_AutoEnabledAndConstructionOnly()
    {
        var store = CreateStore();
        var village = new Info("did:1", "Only", 0, 0, IsCapital: true);

        store.Merge(new[] { village });

        Assert.True(store.GetEnabled(village));
        Assert.Equal(new[] { "construction" }, store.GetEnabledGroups(village));
        Assert.False(store.GetNpcTrade(village));
        Assert.False(store.GetConstructFaster(village));
    }

    [Fact]
    public void Merge_MultipleInitialVillages_AllAutoDisabledAndConstructionOnly()
    {
        var store = CreateStore();
        var capital = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var second = new Info("did:2", "Second", 5, -3, IsCapital: false);

        store.Merge(new[] { capital, second });

        Assert.False(store.GetEnabled(capital));
        Assert.False(store.GetEnabled(second));
        Assert.Equal(new[] { "construction" }, store.GetEnabledGroups(capital));
        Assert.Equal(new[] { "construction" }, store.GetEnabledGroups(second));
    }

    [Fact]
    public void Merge_VillageDiscoveredLater_AutoDisabled()
    {
        var store = CreateStore();
        var first = new Info("did:1", "First", 0, 0, IsCapital: true);
        var later = new Info("did:2", "Later", 5, -3, IsCapital: false);

        store.Merge(new[] { first });
        store.Merge(new[] { first, later });

        Assert.True(store.GetEnabled(first));
        Assert.False(store.GetEnabled(later));
        Assert.Equal(new[] { "construction" }, store.GetEnabledGroups(later));
        Assert.False(store.GetNpcTrade(later));
        Assert.False(store.GetConstructFaster(later));
    }

    [Fact]
    public void GetEnabled_UnknownVillage_DefaultsToDisabled()
    {
        var store = CreateStore();

        Assert.False(store.GetEnabled(new Info("did:9", "New capital", null, null, IsCapital: true)));
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
    public void DisableVillagesMissingFromConfirmedList_DisablesOnlyAbsentVillages()
    {
        var store = CreateStore();
        var capital = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var live = new Info("did:2", "Live", 5, -3, IsCapital: false);
        var missing = new Info("did:3", "Missing", 7, -4, IsCapital: false);
        store.Merge(new[] { capital, live, missing });
        store.SetEnabled(capital, enabled: true);
        store.SetEnabled(live, enabled: true);
        store.SetEnabled(missing, enabled: true);

        var disabled = store.DisableVillagesMissingFromConfirmedList(new[] { capital, live });

        Assert.Equal(new[] { "Missing" }, disabled);
        Assert.True(store.GetEnabled(capital));
        Assert.True(store.GetEnabled(live));
        Assert.False(store.GetEnabled(missing));
        Assert.Equal("xy:7|-4", store.ResolveCanonicalKey("name:missing"));

        var reloaded = CreateStore();
        Assert.False(reloaded.GetEnabled(missing));
    }

    [Fact]
    public void ConfirmedMissingVillage_IsFlagged_PrunedAfterRetention_AndClearedOnReappearance()
    {
        var store = CreateStore();
        var capital = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var lost = new Info("did:3", "Lost", 7, -4, IsCapital: false);
        store.Merge(new[] { capital, lost });
        store.SetEnabled(capital, enabled: true);
        store.SetEnabled(lost, enabled: true);

        // Confirmed list without 'lost' → flagged missing as of now.
        store.DisableVillagesMissingFromConfirmedList(new[] { capital });

        // Still inside the retention window (cutoff in the past) → not yet pruneable.
        Assert.Empty(store.GetVillagesConfirmedMissingSince(DateTimeOffset.UtcNow - TimeSpan.FromDays(3)));

        // Past retention (cutoff in the future) → returned for pruning.
        var missing = store.GetVillagesConfirmedMissingSince(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
        Assert.Equal("xy:7|-4", Assert.Single(missing).Key);

        // Reappears in a later confirmed list → the missing flag is cleared.
        store.DisableVillagesMissingFromConfirmedList(new[] { capital, lost });
        Assert.Empty(store.GetVillagesConfirmedMissingSince(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1)));

        // Goes missing again and is pruned: the record is gone after reload.
        store.DisableVillagesMissingFromConfirmedList(new[] { capital });
        Assert.Equal(1, store.RemoveVillages(new[] { "xy:7|-4" }));
        var reloaded = CreateStore();
        Assert.Empty(reloaded.GetVillagesConfirmedMissingSince(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void DuplicateName_LostVillage_FlaggedMissing_EvenWhenLiveTwinSharesName()
    {
        var store = CreateStore();
        var capital = new Info("did:1", "Capital", 0, 0, IsCapital: true);
        var lost = new Info("did:2", "240", 24, 29, IsCapital: false);
        var refounded = new Info("did:3", "240", 169, 145, IsCapital: false);
        store.Merge(new[] { capital, lost, refounded });

        // Confirmed list has the refounded 240 (xy:169|145) but not the lost 240 (xy:24|29). The lost
        // village must still count as missing on its COORDINATE key even though a same-named village is live.
        store.DisableVillagesMissingFromConfirmedList(new[] { capital, refounded });

        var missing = store.GetVillagesConfirmedMissingSince(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
        Assert.Equal("xy:24|29", Assert.Single(missing).Key);
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
        store.Merge(new[] { capital });
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
    public void Reload_MigratesMissingGroupsAndNpcToNewDefaults()
    {
        var path = AccountStoragePaths.VillageSettingsPath(_root, _activeAccount);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "villages": [
            { "key": "did:1", "name": "Legacy", "coordX": 0, "coordY": 0, "isCapital": true, "isEnabled": true, "lastSeenUtc": "2026-06-14T07:51:00+00:00" }
          ]
        }
        """);

        var village = new Info("did:1", "Legacy", 0, 0, IsCapital: true);
        var store = CreateStore();

        Assert.Equal(new[] { "construction" }, store.GetEnabledGroups(village));
        Assert.False(store.GetNpcTrade(village));
        Assert.False(store.GetConstructFaster(village));

        var reloaded = CreateStore();
        Assert.Equal(new[] { "construction" }, reloaded.GetEnabledGroups(village));
        Assert.False(reloaded.GetNpcTrade(village));
        Assert.False(reloaded.GetConstructFaster(village));
    }

    [Fact]
    public void ConstructFaster_DefaultOffAndPersistsPerVillage()
    {
        var store = CreateStore();
        var village = new Info("did:2", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { village });

        Assert.False(store.GetConstructFaster(village));
        Assert.False(store.IsConstructFasterEnabledByKey("name:second", defaultIfUnknown: false));

        store.SetConstructFaster(village, enabled: true);

        var reloaded = CreateStore();
        Assert.True(reloaded.GetConstructFaster(village));
        Assert.True(reloaded.IsConstructFasterEnabledByKey("name:second", defaultIfUnknown: false));
    }

    [Fact]
    public void HeroResourceSettings_DefaultConstructionOnlyAndPersistPerVillageSettings()
    {
        var store = CreateStore();
        var village = new Info("did:2", "Second", 5, -3, IsCapital: false);
        var defaults = new VillageSettingsStore.HeroResourceSettings(
            IsEnabled: true,
            UseConstruction: true,
            UseSmithy: false,
            UseBrewery: false,
            UseTownHall: false,
            MaxUseEnabled: true,
            MaxUsePerResource: 5000);

        store.Merge(new[] { village });

        var initial = store.GetHeroResourceSettings(village, defaults);
        Assert.True(initial.IsEnabled);
        Assert.True(initial.UseConstruction);
        Assert.False(initial.UseSmithy);
        Assert.False(initial.UseBrewery);
        Assert.False(initial.UseTownHall);
        Assert.True(initial.MaxUseEnabled);
        Assert.Equal(5000, initial.MaxUsePerResource);

        store.SetHeroResourceSettings(village, new VillageSettingsStore.HeroResourceSettings(
            IsEnabled: false,
            UseConstruction: false,
            UseSmithy: true,
            UseBrewery: false,
            UseTownHall: true,
            MaxUseEnabled: false,
            MaxUsePerResource: 12000));

        var reloaded = CreateStore();
        var saved = reloaded.GetHeroResourceSettings("name:second", "Second", defaults);
        Assert.False(saved.IsEnabled);
        Assert.False(saved.UseConstruction);
        Assert.True(saved.UseSmithy);
        Assert.False(saved.UseBrewery);
        Assert.True(saved.UseTownHall);
        Assert.True(saved.MaxUseEnabled);
        Assert.Equal(12000, saved.MaxUsePerResource);
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
    public void GetEnabledGroups_ByVillage_ResolvesByNameWhenKeyFormDiffers()
    {
        // Record stored coordinate-keyed (xy:5|-3) with an explicit override.
        var store = CreateStore();
        var coordVillage = new Info("xy:5|-3", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { coordVillage });
        store.SetEnabledGroups(coordVillage, new[] { "construction" });

        // The Village settings popup and the dashboard can hand us the same village with a different key
        // form (no coords / newdid / name) depending on which item generation they read. The name must
        // still resolve to the stored coordinate record so both paths agree.
        var noCoords = new Info("did:777", "Second", null, null, IsCapital: false);
        var groups = store.GetEnabledGroups(noCoords);

        Assert.NotNull(groups);
        Assert.Equal(new[] { "construction" }, groups!);
    }

    [Fact]
    public void SetEnabledGroups_ByNameMatch_UpdatesExistingRecordInsteadOfSplitting()
    {
        var store = CreateStore();
        var coordVillage = new Info("xy:5|-3", "Second", 5, -3, IsCapital: false);
        store.Merge(new[] { coordVillage });
        store.SetEnabledGroups(coordVillage, new[] { "construction" });

        // A write coming from a coordless item generation must update the SAME record (matched by name),
        // not create a second one under a newdid/name key.
        var noCoords = new Info("did:777", "Second", null, null, IsCapital: false);
        store.SetEnabledGroups(noCoords, new[] { "construction", "troops" });

        var reloaded = CreateStore();
        var groups = reloaded.GetEnabledGroups(coordVillage);
        Assert.NotNull(groups);
        Assert.Equal(new[] { "construction", "troops" }, groups!);
        // The coordinate key still resolves (no split record shadowing it).
        Assert.Contains("troops", reloaded.GetEnabledGroups("xy:5|-3")!);
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
