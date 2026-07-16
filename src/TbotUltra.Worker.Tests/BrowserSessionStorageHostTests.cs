using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserSessionStorageHostTests
{
    private const string AccountHost = "ts50.x5.arabics.travian.com";

    [Theory]
    [InlineData("ts50.x5.arabics.travian.com", true)]
    [InlineData(".travian.com", true)]
    [InlineData("lobby.legends.travian.com", true)]
    [InlineData("session.legends.travian.com", true)]
    [InlineData("auth.travian.com", true)]
    [InlineData("ts51.x5.arabics.travian.com", false)]
    [InlineData("example.com", false)]
    public void KeepHostForAccount_KeepsLobbyAndDropsSiblingWorlds(string host, bool expected)
    {
        Assert.Equal(expected, BrowserSession.KeepHostForAccount(host, AccountHost));
    }
}
