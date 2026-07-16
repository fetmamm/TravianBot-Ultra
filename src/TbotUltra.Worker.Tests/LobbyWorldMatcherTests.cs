using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class LobbyWorldMatcherTests
{
    [Theory]
    [InlineData("https://lobby.legends.travian.com/account", true)]
    [InlineData("https://lobby.legends.travian.com/account/gameworlds", true)]
    [InlineData("https://lobby.legends.travian.com/", false)]
    [InlineData("https://ts50.x5.arabics.travian.com/account", false)]
    public void IsLobbyAccountUrl_RecognizesLogoutLanding(string url, bool expected)
    {
        Assert.Equal(expected, TravianClient.IsLobbyAccountUrl(url));
    }

    [Theory]
    [InlineData("Arabics 50", "ts50.x5.arabics.travian.com", null, true)]
    [InlineData("Arabics 50 x5", "ts50.x5.arabics.travian.com", null, true)]
    [InlineData("Europe 4", "ts4.x1.europe.travian.com", "Europe 4", true)]
    [InlineData("Arabics 51", "ts50.x5.arabics.travian.com", null, false)]
    [InlineData("Europe 50", "ts50.x5.arabics.travian.com", null, false)]
    public void IsLobbyWorldNameMatch_NormalizesHostAndWorldName(
        string worldName,
        string serverHost,
        string? serverName,
        bool expected)
    {
        Assert.Equal(expected, TravianClient.IsLobbyWorldNameMatch(worldName, serverHost, serverName));
    }
}
