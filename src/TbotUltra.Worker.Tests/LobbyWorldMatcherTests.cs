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
    [InlineData("SCHILD", "schild.x3.netherlands.travian.com", "Schild X3", true)]
    [InlineData("SCHILD x3", "schild.x3.netherlands.travian.com", "Schild X3", true)]
    [InlineData("SCHILD x5", "schild.x3.netherlands.travian.com", "Schild X3", false)]
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

    [Theory]
    [InlineData("https://ts50.x5.arabics.travian.com/", true)]
    [InlineData("https://ts50.x5.arabics.travian.com/dorf1.php", true)]
    [InlineData("https://ts50.x5.arabics.travian.com/dorf2.php", true)]
    [InlineData("https://ts51.x5.arabics.travian.com/", false)]
    [InlineData("https://lobby.legends.travian.com/account", false)]
    public void IsConfiguredGameOrigin_AcceptsConfiguredHostRegardlessOfPath(string landedUrl, bool expected)
    {
        Assert.Equal(
            expected,
            TravianClient.IsConfiguredGameOrigin(
                landedUrl,
                "https://ts50.x5.arabics.travian.com"));
    }
}
