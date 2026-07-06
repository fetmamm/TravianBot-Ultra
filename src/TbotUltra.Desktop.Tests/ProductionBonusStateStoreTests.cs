using System.Linq;
using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProductionBonusStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-prodbonus-tests", Guid.NewGuid().ToString("N"));
    private const string Account = "alice";

    public ProductionBonusStateStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SaveLoad_RoundTripsTimers()
    {
        var now = DateTimeOffset.UtcNow;
        var timers = new[]
        {
            new ProductionBonusResourceTimer("lumber", 15, now.AddHours(8), now.AddHours(24)),
            new ProductionBonusResourceTimer("clay", 25, now.AddHours(72), now.AddHours(72)),
        };

        ProductionBonusStateStore.Save(_root, Account, timers);
        var loaded = ProductionBonusStateStore.Load(_root, Account);

        Assert.Equal(2, loaded.Count);
        var lumber = loaded.Single(t => t.Resource == "lumber");
        Assert.Equal(15, lumber.Bonus);
        Assert.Equal(now.AddHours(24).ToUnixTimeSeconds(), lumber.NextAttemptAtUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public void Clear_WipesTimers_ButKeepsSettings()
    {
        ProductionBonusStateStore.SaveSettings(_root, Account, 15, 45);
        ProductionBonusStateStore.Save(_root, Account, new[]
        {
            new ProductionBonusResourceTimer("iron", 15, DateTimeOffset.UtcNow.AddHours(8), DateTimeOffset.UtcNow.AddHours(24)),
        });

        ProductionBonusStateStore.Clear(_root, Account);

        Assert.Empty(ProductionBonusStateStore.Load(_root, Account));
        var settings = ProductionBonusStateStore.LoadSettings(_root, Account);
        Assert.Equal(15, settings.DelayMinMinutes);
        Assert.Equal(45, settings.DelayMaxMinutes);
    }

    [Fact]
    public void Save_PreservesExistingSettings()
    {
        ProductionBonusStateStore.SaveSettings(_root, Account, 20, 50);
        ProductionBonusStateStore.Save(_root, Account, new[]
        {
            new ProductionBonusResourceTimer("crop", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(4)),
        });

        var settings = ProductionBonusStateStore.LoadSettings(_root, Account);
        Assert.Equal(20, settings.DelayMinMinutes);
        Assert.Equal(50, settings.DelayMaxMinutes);
    }

    [Fact]
    public void LoadSettings_DefaultsWhenMissing()
    {
        var settings = ProductionBonusStateStore.LoadSettings(_root, Account);
        Assert.Equal(ProductionBonusStateStore.DefaultDelayMinMinutes, settings.DelayMinMinutes);
        Assert.Equal(ProductionBonusStateStore.DefaultDelayMaxMinutes, settings.DelayMaxMinutes);
    }

    [Fact]
    public void SaveSettings_NormalizesSwappedAndClampedValues()
    {
        ProductionBonusStateStore.SaveSettings(_root, Account, delayMinMinutes: 90, delayMaxMinutes: 30);
        var settings = ProductionBonusStateStore.LoadSettings(_root, Account);
        Assert.Equal(30, settings.DelayMinMinutes);
        Assert.Equal(90, settings.DelayMaxMinutes);

        ProductionBonusStateStore.SaveSettings(_root, Account, delayMinMinutes: -5, delayMaxMinutes: 999999);
        settings = ProductionBonusStateStore.LoadSettings(_root, Account);
        Assert.Equal(0, settings.DelayMinMinutes);
        Assert.Equal(24 * 60, settings.DelayMaxMinutes);
    }

    [Fact]
    public void ShouldAttemptNow_TrueWhenFileMissing()
    {
        Assert.True(ProductionBonusStateStore.ShouldAttemptNow(_root, Account, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ShouldAttemptNow_FalseWhenAllNextAttemptsInFuture()
    {
        var now = DateTimeOffset.UtcNow;
        ProductionBonusStateStore.Save(_root, Account, new[]
        {
            new ProductionBonusResourceTimer("lumber", 15, now.AddHours(8), now.AddHours(24)),
            new ProductionBonusResourceTimer("clay", 25, now.AddHours(72), now.AddHours(72)),
        });

        Assert.False(ProductionBonusStateStore.ShouldAttemptNow(_root, Account, now));
    }

    [Fact]
    public void ShouldAttemptNow_TrueWhenAnyNextAttemptPassed()
    {
        var now = DateTimeOffset.UtcNow;
        ProductionBonusStateStore.Save(_root, Account, new[]
        {
            new ProductionBonusResourceTimer("lumber", 15, now.AddHours(8), now.AddHours(24)),
            new ProductionBonusResourceTimer("iron", 0, now, now.AddMinutes(-1)),
        });

        Assert.True(ProductionBonusStateStore.ShouldAttemptNow(_root, Account, now));
    }

    [Fact]
    public void ShouldAttemptNow_FalseWhenFileCorrupt()
    {
        var path = AccountStoragePaths.ProductionBonusStatePath(_root, Account);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json");

        // Unknown state must not trigger a run.
        Assert.False(ProductionBonusStateStore.ShouldAttemptNow(_root, Account, DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
