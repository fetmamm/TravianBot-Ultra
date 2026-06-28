using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class EnvAccountStoreProxyTests : IDisposable
{
    private readonly string _envPath;

    public EnvAccountStoreProxyTests()
    {
        _envPath = Path.Combine(Path.GetTempPath(), $"tbot-env-store-proxy-{Guid.NewGuid():N}.env");
    }

    [Fact]
    public void SaveAndList_RoundTripsProxyFields()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice",
            Username = "alice",
            Password = "secret",
            ServerUrl = "https://ts1.travian.eu",
            ProxyEnabled = true,
            ProxyServer = "1.2.3.4:8080",
        }, setActive: true);

        var loaded = store.ListAccounts().Single(a => a.Name == "alice");
        Assert.True(loaded.ProxyEnabled);
        Assert.Equal("1.2.3.4:8080", loaded.ProxyServer);
    }

    [Fact]
    public void SaveAccount_PreservesOtherAccountsProxy()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice", Username = "alice", Password = "p", ServerUrl = "https://ts1.travian.eu",
            ProxyEnabled = true, ProxyServer = "1.1.1.1:8080",
        }, setActive: true);
        store.SaveAccount(new AccountEntry
        {
            Name = "bob", Username = "bob", Password = "p", ServerUrl = "https://ts2.travian.eu",
            ProxyEnabled = false, ProxyServer = "",
        }, setActive: false);

        // Re-save bob; alice's proxy must survive the whole-file rewrite.
        store.SaveAccount(new AccountEntry
        {
            Name = "bob", Username = "bob", Password = "p2", ServerUrl = "https://ts2.travian.eu",
            ProxyEnabled = true, ProxyServer = "2.2.2.2:9090",
        }, setActive: false);

        var accounts = store.ListAccounts();
        var alice = accounts.Single(a => a.Name == "alice");
        var bob = accounts.Single(a => a.Name == "bob");
        Assert.True(alice.ProxyEnabled);
        Assert.Equal("1.1.1.1:8080", alice.ProxyServer);
        Assert.True(bob.ProxyEnabled);
        Assert.Equal("2.2.2.2:9090", bob.ProxyServer);
    }

    public void Dispose()
    {
        if (File.Exists(_envPath))
        {
            File.Delete(_envPath);
        }
    }
}
