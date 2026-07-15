using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountAutomationHoldStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-hold-{Guid.NewGuid():N}");

    [Fact]
    public void HoldSurvivesStoreReloadAndClearKeepsAccountDirectory()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var first = new AccountAutomationHoldStore(_root);
        first.Save(new AccountAutomationHold("account-one", "Restricted", "Manual review", createdAt));
        first.Save(new AccountAutomationHold("account-two", "Challenge", "Manual review", createdAt));
        var queuePath = AccountStoragePaths.AccountQueuePath(_root, "account-one");
        var settingsPath = AccountStoragePaths.AccountSettingsPath(_root, "account-one");
        File.WriteAllText(queuePath, "[]");
        File.WriteAllText(settingsPath, "{}");

        var loaded = new AccountAutomationHoldStore(_root).Load("account-one");
        Assert.NotNull(loaded);
        Assert.Equal("Restricted", loaded!.AccessState);

        first.Clear("account-one");

        Assert.Null(first.Load("account-one"));
        Assert.NotNull(first.Load("account-two"));
        Assert.True(File.Exists(queuePath));
        Assert.True(File.Exists(settingsPath));
        Assert.True(Directory.Exists(Path.Combine(_root, "config", "accounts", "account_one")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
