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
            ProxyId = "proxy-id",
            ProxyServer = "1.2.3.4:8080",
        }, setActive: true);

        var loaded = store.ListAccounts().Single(a => a.Name == "alice");
        Assert.True(loaded.ProxyEnabled);
        Assert.Equal("proxy-id", loaded.ProxyId);
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

    [Fact]
    public void SynchronizeProxyBindings_UpdatesCredentialsForTheCorrectAccountsSharingAnEndpoint()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice", Username = "alice", Password = "p", ServerUrl = "https://ts1.travian.eu",
            ProxyEnabled = true, ProxyId = "alice-proxy", ProxyServer = "http://old:old@proxy.example:8080",
        }, setActive: true);
        store.SaveAccount(new AccountEntry
        {
            Name = "bob", Username = "bob", Password = "p", ServerUrl = "https://ts2.travian.eu",
            ProxyEnabled = true, ProxyId = "bob-proxy", ProxyServer = "http://old:old@proxy.example:8080",
        }, setActive: false);

        var changed = store.SynchronizeProxyBindings([
            new ProxyLibraryEntry
            {
                Id = "alice-proxy", Scheme = "http", Host = "proxy.example", Port = 8080,
                Username = "alice-user", Password = "alice-password",
            },
            new ProxyLibraryEntry
            {
                Id = "bob-proxy", Scheme = "http", Host = "proxy.example", Port = 8080,
                Username = "bob-user", Password = "bob-password",
            },
        ]);

        Assert.Equal(2, changed);
        var accounts = store.ListAccounts();
        Assert.Equal("http://alice-user:alice-password@proxy.example:8080", accounts.Single(a => a.Name == "alice").ProxyServer);
        Assert.Equal("http://bob-user:bob-password@proxy.example:8080", accounts.Single(a => a.Name == "bob").ProxyServer);
    }

    [Fact]
    public void SynchronizeProxyBindings_MigratesUniqueLegacyEndpointToAuthenticatedEntry()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice", Username = "alice", Password = "p", ServerUrl = "https://ts1.travian.eu",
            ProxyEnabled = true, ProxyServer = "http://proxy.example:8080",
        }, setActive: true);
        var proxy = new ProxyLibraryEntry
        {
            Id = "authenticated", Scheme = "http", Host = "proxy.example", Port = 8080,
            Username = "user", Password = "secret",
        };

        Assert.Equal(1, store.SynchronizeProxyBindings([proxy]));

        var account = Assert.Single(store.ListAccounts());
        Assert.Equal(proxy.Id, account.ProxyId);
        Assert.Equal(proxy.Server, account.ProxyServer);
    }

    [Fact]
    public void SynchronizeProxyBindings_RebindsDeletedEntryToItsUniqueReplacement()
    {
        var store = new EnvAccountStore(_envPath);
        store.SaveAccount(new AccountEntry
        {
            Name = "alice", Username = "alice", Password = "p", ServerUrl = "https://ts1.travian.eu",
            ProxyEnabled = true, ProxyId = "deleted", ProxyServer = "http://proxy.example:8080",
        }, setActive: true);
        var replacement = new ProxyLibraryEntry
        {
            Id = "replacement", Scheme = "http", Host = "proxy.example", Port = 8080,
            Username = "user", Password = "secret",
        };

        Assert.Equal(1, store.SynchronizeProxyBindings([replacement]));

        var account = Assert.Single(store.ListAccounts());
        Assert.Equal(replacement.Id, account.ProxyId);
        Assert.Equal(replacement.Server, account.ProxyServer);
    }

    public void Dispose()
    {
        if (File.Exists(_envPath))
        {
            File.Delete(_envPath);
        }
    }
}
