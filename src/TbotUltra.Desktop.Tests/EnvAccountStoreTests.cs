using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class EnvAccountStoreTests : IDisposable
{
    private readonly string _envPath;

    public EnvAccountStoreTests()
    {
        _envPath = Path.Combine(Path.GetTempPath(), $"tbot-env-store-{Guid.NewGuid():N}.env");
    }

    private static AccountEntry Account(string name) => new()
    {
        Name = name,
        Username = name,
        Password = "pw",
        ServerName = "Server",
        ServerUrl = "https://ts1.travian.eu",
    };

    [Fact]
    public void SaveAccount_SetActive_BecomesActiveAccount()
    {
        var store = new EnvAccountStore(_envPath);

        store.SaveAccount(Account("alice"), setActive: true);

        Assert.Equal("alice", store.ActiveAccountName());
        Assert.True(store.ListAccounts().Single().IsActive);
    }

    [Fact]
    public void SaveAccount_WithoutSetActive_KeepsExistingActive()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);

        store.SaveAccount(Account("bob"), setActive: false);

        Assert.Equal("alice", store.ActiveAccountName());
        Assert.Equal(2, store.ListAccounts().Count);
    }

    [Fact]
    public void SetActive_SwitchesActiveAccount()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);
        store.SaveAccount(Account("bob"), setActive: false);

        store.SetActive("bob");

        Assert.Equal("bob", store.ActiveAccountName());
    }

    [Fact]
    public void SetActive_UnknownAccount_Throws()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);

        Assert.Throws<InvalidOperationException>(() => store.SetActive("ghost"));
    }

    [Fact]
    public void DeleteAccount_RemovesAccount()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);
        store.SaveAccount(Account("bob"), setActive: false);

        store.DeleteAccount("bob");

        Assert.DoesNotContain(store.ListAccounts(), a => a.Name == "bob");
    }

    [Fact]
    public void DeleteAccount_ActiveAccount_ReassignsActiveToRemaining()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);
        store.SaveAccount(Account("bob"), setActive: false);

        store.DeleteAccount("alice");

        Assert.Equal("bob", store.ActiveAccountName());
    }

    [Fact]
    public void DeleteAccount_LastAccount_ClearsActive()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);

        store.DeleteAccount("alice");

        Assert.Empty(store.ActiveAccountName());
        Assert.Empty(store.ListAccounts());
    }

    [Fact]
    public void DeleteAccount_Unknown_Throws()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("alice"), setActive: true);

        Assert.Throws<InvalidOperationException>(() => store.DeleteAccount("ghost"));
    }

    [Fact]
    public void ListAccounts_ReturnsSavedFields_AndTrimsTrailingSlashOnUrl()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice",
            Username = "aliceUser",
            Password = "secret",
            ServerName = "Asia 50",
            ServerUrl = "https://ts50.travian.com/",
        }, setActive: true);

        var alice = store.ListAccounts().Single();
        Assert.Equal("alice", alice.Name);
        Assert.Equal("aliceUser", alice.Username);
        Assert.Equal("secret", alice.Password);
        Assert.Equal("Asia 50", alice.ServerName);
        Assert.Equal("https://ts50.travian.com", alice.ServerUrl);
    }

    [Fact]
    public void SaveAccount_NormalizesNameToLowercaseUnderscored()
    {
        var store = new EnvAccountStore(_envPath);

        store.SaveAccount(Account("My Account"), setActive: true);

        Assert.Equal("my_account", store.ActiveAccountName());
    }

    [Fact]
    public void SaveAccount_EmptyName_Throws()
    {
        var store = new EnvAccountStore(_envPath);

        Assert.Throws<InvalidOperationException>(() => store.SaveAccount(Account(""), setActive: true));
    }

    [Fact]
    public void SaveAccount_RoundTripsPasswordWithoutTrimmingOrCorruption()
    {
        var store = new EnvAccountStore(_envPath);
        const string password = "  p=a#s\\\"'word\\path\nsecond line  ";

        store.SaveAccount(new AccountEntry
        {
            Name = "alice",
            Username = "alice",
            Password = password,
            ServerName = "Server",
            ServerUrl = "https://ts1.travian.eu",
        }, setActive: true);

        var reloaded = new EnvAccountStore(_envPath).ListAccounts().Single();

        Assert.Equal(password, reloaded.Password);
    }

    [Fact]
    public void UpdateAccountServer_ChangesOnlyTheSelectedAccountsWorld()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice",
            Username = "alice-user",
            Password = "secret",
            ServerName = "Wrong world",
            ServerUrl = "https://wrong.x1.europe.travian.com",
            ProxyEnabled = true,
            ProxyServer = "http://proxy.example:8080",
            NeverUseOwnIp = true,
        }, setActive: true);
        store.SaveAccount(Account("bob"), setActive: false);

        store.UpdateAccountServer(
            "alice",
            "TRAVIAN SCHILD",
            "https://schild.x3.netherlands.travian.com/dorf1.php");

        var accounts = store.ListAccounts();
        var alice = accounts.Single(account => account.Name == "alice");
        Assert.Equal("TRAVIAN SCHILD", alice.ServerName);
        Assert.Equal("https://schild.x3.netherlands.travian.com", alice.ServerUrl);
        Assert.Equal("alice-user", alice.Username);
        Assert.Equal("secret", alice.Password);
        Assert.True(alice.ProxyEnabled);
        Assert.Equal("http://proxy.example:8080", alice.ProxyServer);
        Assert.True(alice.NeverUseOwnIp);
        Assert.Equal("https://ts1.travian.eu", accounts.Single(account => account.Name == "bob").ServerUrl);
    }

    [Fact]
    public void ConcurrentStores_DoNotLoseAccountsDuringReadModifyWrite()
    {
        var first = new EnvAccountStore(_envPath);
        var second = new EnvAccountStore(_envPath);

        Parallel.Invoke(
            () => first.SaveAccount(Account("alice"), setActive: true),
            () => second.SaveAccount(Account("bob"), setActive: false));

        var accounts = new EnvAccountStore(_envPath).ListAccounts();
        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, account => account.Name == "alice");
        Assert.Contains(accounts, account => account.Name == "bob");
    }

    [Fact]
    public void SaveAccount_RejectsSilentOverwriteWhenNormalizedKeysCollide()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(Account("john.doe"), setActive: true);

        var collision = Account("john_doe");

        Assert.Throws<InvalidOperationException>(() => store.SaveAccount(collision, setActive: false));
        Assert.Equal("john.doe", store.ListAccounts().Single().Username);
    }

    public void Dispose()
    {
        if (File.Exists(_envPath))
        {
            File.Delete(_envPath);
        }
    }
}
