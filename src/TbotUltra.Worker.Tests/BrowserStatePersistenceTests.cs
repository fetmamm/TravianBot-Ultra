using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserStatePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tbot-browser-state-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ClearAllSavedStates_DeletesOnlyBrowserAuthFiles()
    {
        var firstAccount = AccountStoragePaths.AccountDirectory(_root, "first@example.com");
        var secondAccount = AccountStoragePaths.AccountDirectory(_root, "second@example.com");
        var firstState = AccountStoragePaths.BrowserStatePath(_root, "first@example.com");
        var secondState = AccountStoragePaths.BrowserStatePath(_root, "second@example.com");
        var orphanedTempState = firstState + ".123.tmp";
        var settings = Path.Combine(firstAccount, "settings.json");
        var legacyState = AccountStoragePaths.LegacyBrowserStatePath(_root, "first@example.com");

        Directory.CreateDirectory(Path.GetDirectoryName(firstState)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondState)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyState)!);
        File.WriteAllText(firstState, "{}");
        File.WriteAllText(secondState, "{}");
        File.WriteAllText(orphanedTempState, "{}");
        File.WriteAllText(settings, "{}");
        File.WriteAllText(legacyState, "{}");

        var deleted = BrowserStatePersistence.ClearAllSavedStates(_root);

        Assert.Equal(4, deleted);
        Assert.False(File.Exists(firstState));
        Assert.False(File.Exists(secondState));
        Assert.False(File.Exists(orphanedTempState));
        Assert.False(File.Exists(legacyState));
        Assert.True(File.Exists(settings));
        Assert.True(Directory.Exists(secondAccount));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
