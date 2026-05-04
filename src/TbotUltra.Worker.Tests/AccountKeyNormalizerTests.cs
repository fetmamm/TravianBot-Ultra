using TbotUltra.Core.Accounts;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class AccountKeyNormalizerTests
{
    [Theory]
    [InlineData("alice", "https://ts1.x1.travian.eu", "alice__ts1_x1_travian_eu")]
    [InlineData("Alice", "https://TS1.X1.TRAVIAN.EU", "alice__ts1_x1_travian_eu")]
    [InlineData("alice", "http://ts5.travian.de", "alice__ts5_travian_de")]
    [InlineData("alice", "https://ts1.travian.eu:8443", "alice__ts1_travian_eu_8443")]
    [InlineData("alice", "https://ts1.travian.eu/", "alice__ts1_travian_eu")]
    [InlineData("Bob 99", "https://ts1.travian.eu", "bob_99__ts1_travian_eu")]
    [InlineData("user.name+tag", "https://ts1.travian.eu", "user_name_tag__ts1_travian_eu")]
    [InlineData("alice", "", "alice")]
    [InlineData("alice", "   ", "alice")]
    [InlineData("alice", "not-a-url", "alice__not_a_url")]
    public void MakeKey_ProducesStableServerAwareKey(string username, string serverUrl, string expected)
    {
        Assert.Equal(expected, AccountKeyNormalizer.MakeKey(username, serverUrl));
    }

    [Fact]
    public void MakeKey_DistinguishesSameUserOnDifferentServers()
    {
        var a = AccountKeyNormalizer.MakeKey("alice", "https://ts1.travian.eu");
        var b = AccountKeyNormalizer.MakeKey("alice", "https://ts5.travian.de");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MakeKey_StableForSameUserOnSameServerWithCaseAndTrailingSlash()
    {
        var a = AccountKeyNormalizer.MakeKey("Alice", "https://TS1.travian.eu/");
        var b = AccountKeyNormalizer.MakeKey("alice", "https://ts1.travian.eu");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("___")]
    [InlineData("!!!")]
    public void MakeKey_ThrowsWhenUsernameNormalizesToEmpty(string username)
    {
        Assert.Throws<InvalidOperationException>(() => AccountKeyNormalizer.MakeKey(username, "https://ts1.travian.eu"));
    }

    [Theory]
    [InlineData("https://ts1.x1.travian.eu", "ts1_x1_travian_eu")]
    [InlineData("https://ts1.travian.eu:8443", "ts1_travian_eu_8443")]
    [InlineData("http://ts1.travian.eu:80", "ts1_travian_eu")]
    [InlineData("https://ts1.travian.eu:443", "ts1_travian_eu")]
    [InlineData("not-a-url", "not_a_url")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeServerHost_ExtractsHostAndNonDefaultPort(string? input, string expected)
    {
        Assert.Equal(expected, AccountKeyNormalizer.NormalizeServerHost(input!));
    }
}
