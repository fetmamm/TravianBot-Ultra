using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class IncomingAttackMonitoringSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-incoming-monitoring-{Guid.NewGuid():N}");

    [Fact]
    public void MissingSettings_DefaultsVillagesToEnabledAndSoundToOff()
    {
        var settings = new IncomingAttackMonitoringSettingsStore(_root)
            .Load("account", "https://one.example");

        Assert.Empty(settings.DisabledVillageKeys);
        Assert.False(settings.SoundEnabled);
        Assert.Equal(1, settings.SoundCooldownMinutes);
    }

    [Fact]
    public void SaveLoad_IsolatesDisabledVillageKeysByWorld()
    {
        var store = new IncomingAttackMonitoringSettingsStore(_root);
        store.Save("account", "https://one.example", new[] { "xy:1|2", "xy:3|4" }, true, 5);

        var saved = store.Load("account", "https://one.example");
        Assert.Equal(2, saved.DisabledVillageKeys.Count);
        Assert.True(saved.SoundEnabled);
        Assert.Equal(5, saved.SoundCooldownMinutes);
        var otherWorld = store.Load("account", "https://two.example");
        Assert.Empty(otherWorld.DisabledVillageKeys);
        Assert.False(otherWorld.SoundEnabled);
    }

    [Fact]
    public void Load_QuarantinesCorruptSettings()
    {
        var path = AccountStoragePaths.IncomingAttackMonitoringSettingsPath(_root, "account", "https://one.example");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{broken");

        var settings = new IncomingAttackMonitoringSettingsStore(_root).Load("account", "https://one.example");

        Assert.Empty(settings.DisabledVillageKeys);
        Assert.False(settings.SoundEnabled);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
    }

    [Fact]
    public void VersionOneSettings_AreLoadedWithSoundOffAndDefaultCooldown()
    {
        var path = AccountStoragePaths.IncomingAttackMonitoringSettingsPath(_root, "account", "https://one.example");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "disabledVillageKeys": ["xy:1|2"]
            }
            """);

        var settings = new IncomingAttackMonitoringSettingsStore(_root).Load("account", "https://one.example");

        Assert.Contains("xy:1|2", settings.DisabledVillageKeys);
        Assert.False(settings.SoundEnabled);
        Assert.Equal(1, settings.SoundCooldownMinutes);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void InvalidCooldown_IsNormalizedToOneMinute()
    {
        var store = new IncomingAttackMonitoringSettingsStore(_root);
        store.Save("account", "https://one.example", [], true, 99);

        var settings = store.Load("account", "https://one.example");

        Assert.True(settings.SoundEnabled);
        Assert.Equal(1, settings.SoundCooldownMinutes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
