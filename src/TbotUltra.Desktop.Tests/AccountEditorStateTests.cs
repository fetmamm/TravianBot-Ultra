using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountEditorStateTests
{
    [Fact]
    public void HasChanges_UsesCaseInsensitiveServerUrlButExactCredentialsAndProxy()
    {
        var baseline = Snapshot(serverUrl: "https://Example.com", username: "user");

        Assert.False(AccountEditorState.HasChanges(baseline, baseline with { ServerUrl = "https://example.com" }));
        Assert.True(AccountEditorState.HasChanges(baseline, baseline with { Username = "User" }));
        Assert.True(AccountEditorState.HasChanges(baseline, baseline with { ProxyServer = "socks5://other:1080" }));
    }

    [Fact]
    public void BuildSavedProxyOptions_PutsActiveAccountsAssignedProxiesFirst()
    {
        var entries = new[]
        {
            Entry("B", "b.example", assigned: null, latencyMs: 10),
            Entry("Z", "z.example", assigned: "active", latencyMs: 500),
            Entry("A", "a.example", assigned: "other", latencyMs: 20),
        };
        var accounts = new[]
        {
            new AccountEntry { Name = "active", Username = "player@example.com", ServerName = "Arabia 50 (5x)" },
        };

        var options = AccountEditorState.BuildSavedProxyOptions(entries, "active", accounts);

        Assert.Null(options[0].Entry);
        Assert.Equal("Z", options[1].Entry!.Name);
        Assert.Equal(new[] { "B", "A" }, options.Skip(2).Select(option => option.Entry!.Name));
        Assert.Equal("Unknown · z.example:1080 · player@example.com · Arabia 50 (5x) · 500 ms", options[1].DisplayText);
    }

    [Fact]
    public void BuildSavedProxyOptions_ShowsUnassignedAndUntestedProxy()
    {
        var entry = Entry("Shared", "shared.example", assigned: null);

        var option = AccountEditorState.BuildSavedProxyOptions([entry], "active")[1];

        Assert.Equal("Unknown · shared.example:1080 · Unassigned · — · Not tested", option.DisplayText);
    }

    [Fact]
    public void BuildAccountEntry_DisabledEmptyProxyStaysEmpty()
    {
        var result = AccountEditorState.BuildAccountEntry(Input());

        Assert.False(result.ProxyEnabled);
        Assert.Equal(string.Empty, result.ProxyServer);
        Assert.Equal("user", result.Username);
    }

    [Fact]
    public void BuildAccountEntry_NeverUseOwnIpEnablesValidProxy()
    {
        var result = AccountEditorState.BuildAccountEntry(Input(
            proxyEnabled: false,
            neverUseOwnIp: true,
            proxyHost: "127.0.0.1",
            proxyPort: "1080"));

        Assert.True(result.ProxyEnabled);
        Assert.True(result.NeverUseOwnIp);
        Assert.Equal("socks5://127.0.0.1:1080", result.ProxyServer);
    }

    [Theory]
    [InlineData("proxy:1080", "1080")]
    [InlineData("proxy host", "1080")]
    [InlineData("proxy", "0")]
    [InlineData("proxy", "65536")]
    public void BuildAccountEntry_RejectsInvalidProxyFields(string host, string port)
    {
        Assert.Throws<InvalidOperationException>(() => AccountEditorState.BuildAccountEntry(Input(
            proxyEnabled: true,
            proxyHost: host,
            proxyPort: port)));
    }

    private static AccountEditorSnapshot Snapshot(
        string serverUrl = "https://example.com",
        string username = "user")
        => new(username, "password", serverUrl, true, "socks5://proxy:1080", false);

    private static AccountEditorInput Input(
        bool proxyEnabled = false,
        bool neverUseOwnIp = false,
        string proxyHost = "",
        string proxyPort = "")
        => new(
            "user",
            "password",
            "Official",
            "https://example.com",
            proxyEnabled,
            neverUseOwnIp,
            "socks5",
            proxyHost,
            proxyPort,
            EditingExistingAccount: false,
            ExistingAccountName: string.Empty);

    private static ProxyLibraryEntry Entry(string name, string host, string? assigned, long? latencyMs = null)
        => new()
        {
            Name = name,
            Scheme = "socks5",
            Host = host,
            Port = 1080,
            AssignedAccount = assigned,
            LatencyMs = latencyMs,
        };
}
