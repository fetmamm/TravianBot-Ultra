using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class QueueMigrationTests : IDisposable
{
    private readonly string _root;

    public QueueMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-queue-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Migrate_MovesLegacyGlobalQueue_ToActiveAccountFile()
    {
        var legacy = AccountStoragePaths.LegacyGlobalQueuePath(_root);
        WriteFile(legacy, "[{\"taskName\":\"status\"}]");

        QueueMigration.MigrateLegacyGlobalQueue(_root, "alice");

        var target = AccountStoragePaths.AccountQueuePath(_root, "alice");
        Assert.True(File.Exists(target));
        Assert.Contains("status", File.ReadAllText(target));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists($"{legacy}.bak"));
    }

    [Fact]
    public void Migrate_DoesNotClobber_ExistingAccountQueue()
    {
        var legacy = AccountStoragePaths.LegacyGlobalQueuePath(_root);
        WriteFile(legacy, "[{\"taskName\":\"status\"}]");
        var target = AccountStoragePaths.AccountQueuePath(_root, "alice");
        WriteFile(target, "[{\"taskName\":\"scan_all_villages\"}]");

        QueueMigration.MigrateLegacyGlobalQueue(_root, "alice");

        // Existing per-account queue is preserved; legacy file is retired regardless.
        Assert.Contains("scan_all_villages", File.ReadAllText(target));
        Assert.DoesNotContain("status", File.ReadAllText(target));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists($"{legacy}.bak"));
    }

    [Fact]
    public void Migrate_NoLegacyFile_IsNoOp()
    {
        QueueMigration.MigrateLegacyGlobalQueue(_root, "alice");

        Assert.False(File.Exists(AccountStoragePaths.AccountQueuePath(_root, "alice")));
        Assert.False(File.Exists($"{AccountStoragePaths.LegacyGlobalQueuePath(_root)}.bak"));
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
