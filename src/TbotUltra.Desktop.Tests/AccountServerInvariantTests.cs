using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountServerInvariantTests
{
    [Theory]
    [InlineData("https://TS1.x1.europe.travian.com/", "https://ts1.x1.europe.travian.com")]
    [InlineData("https://ts1.x1.europe.travian.com:443/dorf1.php", "https://ts1.x1.europe.travian.com")]
    public void EnsureMatches_AcceptsEquivalentOrigins(string accountUrl, string configUrl)
    {
        AccountServerInvariant.EnsureMatches("alice", accountUrl, configUrl);
    }

    [Fact]
    public void EnsureMatches_RejectsDifferentWorlds()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AccountServerInvariant.EnsureMatches(
                "alice",
                "https://ts1.x1.europe.travian.com",
                "https://ts2.x1.europe.travian.com"));

        Assert.Contains("Login blocked", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/server")]
    public void EnsureMatches_RejectsInvalidAccountServer(string accountUrl)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AccountServerInvariant.EnsureMatches("alice", accountUrl, "https://ts1.x1.europe.travian.com"));
    }
}
