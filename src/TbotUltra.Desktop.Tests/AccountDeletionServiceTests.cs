using System.Text.Json;
using System.Text.Json.Nodes;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountDeletionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _envPath;
    private readonly string _configPath;

    public AccountDeletionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-delete-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        _envPath = Path.Combine(_root, ".env");
        _configPath = Path.Combine(_root, "config", "bot.json");
    }

    [Fact]
    public void DeleteAccount_RemovesEnvEntryAndAccountArtifacts()
    {
        WriteEnv("alice,bob", "alice");
        WriteBotConfigWithReinforcementRules();
        WriteAccountArtifacts("alice");
        var service = CreateService();

        service.DeleteAccount("alice");

        var env = File.ReadAllText(_envPath);
        Assert.DoesNotContain("TBOT_ALICE_USERNAME", env);
        Assert.Contains("TBOT_ACCOUNTS=bob", env);
        Assert.False(Directory.Exists(AccountStoragePaths.AccountDirectory(_root, "alice")));
        Assert.False(File.Exists(AccountStoragePaths.LegacyBrowserStatePath(_root, "alice")));
        Assert.False(File.Exists(AccountStoragePaths.BuildingsSnapshotPath(_root, "alice")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "config", "account-analysis"), "alice*"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "config", "cache", "natar-farms"), "alice*"));

        var config = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        var rules = config[BotOptionPayloadKeys.ReinforcementsTroopRules]!.AsArray();
        Assert.DoesNotContain(rules.OfType<JsonObject>(), rule => rule["accountName"]?.GetValue<string>() == "alice");
        Assert.Contains(rules.OfType<JsonObject>(), rule => rule["accountName"]?.GetValue<string>() == "bob");
    }

    [Fact]
    public void DeleteAccount_BlocksWhenQueueHasActiveItems()
    {
        WriteEnv("alice", "alice");
        WriteBotConfigWithReinforcementRules();
        var accountStore = new EnvAccountStore(_envPath);
        var queueStore = CreateQueueStore(accountStore);
        queueStore.Add("status", null, priority: 0, maxRetries: 0);
        var service = new AccountDeletionService(
            _root,
            accountStore,
            new BotConfigStore(_configPath, _root, accountStore.ActiveAccountName),
            queueStore);

        var ex = Assert.Throws<InvalidOperationException>(() => service.DeleteAccount("alice"));

        Assert.Contains("Cannot delete the active account while", ex.Message);
        Assert.Contains("TBOT_ALICE_USERNAME=alice@example.com", File.ReadAllText(_envPath));
    }

    [Fact]
    public void DeleteAccount_DeleteAnywayClearsActiveQueueAndRemovesAccount()
    {
        WriteEnv("alice,bob", "alice");
        WriteBotConfigWithReinforcementRules();
        WriteAccountArtifacts("alice");
        var accountStore = new EnvAccountStore(_envPath);
        var queueStore = CreateQueueStore(accountStore);
        queueStore.Add("status", null, priority: 0, maxRetries: 0);
        var aliceQueuePath = AccountStoragePaths.AccountQueuePath(_root, "alice");
        var service = new AccountDeletionService(
            _root,
            accountStore,
            new BotConfigStore(_configPath, _root, accountStore.ActiveAccountName),
            queueStore);

        service.DeleteAccount("alice", deleteAnyway: true);

        var env = File.ReadAllText(_envPath);
        Assert.DoesNotContain("TBOT_ALICE_USERNAME", env);
        Assert.Contains("TBOT_ACTIVE_ACCOUNT=bob", env);
        Assert.False(File.Exists(aliceQueuePath));
        Assert.False(Directory.Exists(AccountStoragePaths.AccountDirectory(_root, "alice")));
    }

    [Fact]
    public void DeleteAccount_AllowsInactiveAccountWhenActiveAccountHasQueue()
    {
        WriteEnv("alice,bob", "alice");
        WriteBotConfigWithReinforcementRules();
        WriteAccountArtifacts("bob");
        var accountStore = new EnvAccountStore(_envPath);
        var queueStore = CreateQueueStore(accountStore);
        queueStore.Add("status", null, priority: 0, maxRetries: 0);
        var service = new AccountDeletionService(
            _root,
            accountStore,
            new BotConfigStore(_configPath, _root, accountStore.ActiveAccountName),
            queueStore);

        service.DeleteAccount("bob");

        Assert.False(Directory.Exists(AccountStoragePaths.AccountDirectory(_root, "bob")));
        Assert.Contains("TBOT_ACTIVE_ACCOUNT=alice", File.ReadAllText(_envPath));
        Assert.Single(queueStore.GetAll());
    }

    private AccountDeletionService CreateService()
    {
        var accountStore = new EnvAccountStore(_envPath);
        return new AccountDeletionService(
            _root,
            accountStore,
            new BotConfigStore(_configPath, _root, accountStore.ActiveAccountName),
            CreateQueueStore(accountStore));
    }

    private JsonQueueStore CreateQueueStore(EnvAccountStore accountStore)
    {
        return new JsonQueueStore(
            () => AccountStoragePaths.AccountQueuePath(_root, accountStore.ActiveAccountName()));
    }

    private void WriteEnv(string accounts, string active)
    {
        File.WriteAllText(
            _envPath,
            string.Join(
                Environment.NewLine,
                [
                    "TBOT_ACTIVE_ACCOUNT=" + active,
                    "TBOT_ACCOUNTS=" + accounts,
                    "TBOT_ALICE_USERNAME=alice@example.com",
                    "TBOT_ALICE_PASSWORD=secret",
                    "TBOT_ALICE_SERVER_NAME=Example",
                    "TBOT_ALICE_SERVER_URL=https://example.com",
                    "TBOT_BOB_USERNAME=bob@example.com",
                    "TBOT_BOB_PASSWORD=secret",
                    "TBOT_BOB_SERVER_NAME=Example",
                    "TBOT_BOB_SERVER_URL=https://example.com",
                ]));
    }

    private void WriteBotConfigWithReinforcementRules()
    {
        var config = new JsonObject
        {
            [BotOptionPayloadKeys.ReinforcementsTroopRules] = new JsonArray(
                new JsonObject
                {
                    ["accountName"] = "alice",
                    ["sourceVillageName"] = "A",
                    ["troopType"] = "Clubswinger",
                    ["amountMode"] = "fixed",
                    ["amount"] = 1,
                    ["isEnabled"] = true,
                },
                new JsonObject
                {
                    ["accountName"] = "bob",
                    ["sourceVillageName"] = "B",
                    ["troopType"] = "Clubswinger",
                    ["amountMode"] = "fixed",
                    ["amount"] = 1,
                    ["isEnabled"] = true,
                })
        };
        File.WriteAllText(_configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteAccountArtifacts(string accountName)
    {
        var accountDirectory = AccountStoragePaths.AccountDirectory(_root, accountName);
        Directory.CreateDirectory(accountDirectory);
        File.WriteAllText(Path.Combine(accountDirectory, "marker.txt"), "x");

        WriteFile(AccountStoragePaths.LegacyBrowserStatePath(_root, accountName), "{}");
        WriteFile(AccountStoragePaths.BuildingsSnapshotPath(_root, accountName), "{}");
        WriteFile(AccountStoragePaths.LegacyAnalysisPath(_root, accountName, "https://example.com"), "{}");
        WriteFile(AccountStoragePaths.LegacyNatarFarmCachePath(_root, accountName, "https://example.com", "farm_villages"), "{}");
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
