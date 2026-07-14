using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class LegacyBrowserStorageAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-legacy-browser-{Guid.NewGuid():N}");

    [Fact]
    public void MigrateIfNeeded_CopiesOldOnlyStateAndIsIdempotent()
    {
        var legacy = Path.Combine(_root, "legacy.json");
        var target = Path.Combine(_root, "account", "state.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(legacy, "old-state");

        LegacyBrowserStorageAdapter.MigrateIfNeeded(legacy, target);
        LegacyBrowserStorageAdapter.MigrateIfNeeded(legacy, target);

        Assert.Equal("old-state", File.ReadAllText(target));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void MigrateIfNeeded_PreservesNewStateWhenBothExist()
    {
        var legacy = Path.Combine(_root, "legacy.json");
        var target = Path.Combine(_root, "account", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(legacy, "old-state");
        File.WriteAllText(target, "new-state");

        LegacyBrowserStorageAdapter.MigrateIfNeeded(legacy, target);

        Assert.Equal("new-state", File.ReadAllText(target));
    }

    [Fact]
    public void DeleteIfPresent_IsIdempotent()
    {
        var legacy = Path.Combine(_root, "legacy.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(legacy, "state");

        LegacyBrowserStorageAdapter.DeleteIfPresent(legacy);
        LegacyBrowserStorageAdapter.DeleteIfPresent(legacy);

        Assert.False(File.Exists(legacy));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
