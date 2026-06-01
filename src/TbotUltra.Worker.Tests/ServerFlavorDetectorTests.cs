using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ServerFlavorDetectorTests
{
    [Theory]
    [InlineData("https://vip.ss-travi.com", ServerFlavor.SsTravi)]
    [InlineData("https://ss-travi.com/login.php", ServerFlavor.SsTravi)]
    [InlineData("vip.ss-travi.com", ServerFlavor.SsTravi)]
    [InlineData("https://ts1.travian.com", ServerFlavor.Official)]
    [InlineData("https://ts20.x1.international.travian.com", ServerFlavor.Official)]
    [InlineData("https://ts3.travian.se", ServerFlavor.Official)]
    [InlineData("", ServerFlavor.Official)]
    [InlineData(null, ServerFlavor.Official)]
    [InlineData("not-a-ss-travi-imposter.com", ServerFlavor.Official)]
    public void FromBaseUrl_DetectsFlavor(string? baseUrl, ServerFlavor expected)
    {
        Assert.Equal(expected, ServerFlavorDetector.FromBaseUrl(baseUrl));
    }

    [Theory]
    [InlineData("official", ServerFlavor.Official)]
    [InlineData("ss_travi", ServerFlavor.SsTravi)]
    [InlineData("ss-travi", ServerFlavor.SsTravi)]
    [InlineData("private", ServerFlavor.SsTravi)]
    public void ParseExplicit_ParsesKnownValues(string value, ServerFlavor expected)
    {
        Assert.Equal(expected, ServerFlavorDetector.ParseExplicit(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-unknown")]
    public void ParseExplicit_ReturnsNullForMissingOrUnknown(string? value)
    {
        Assert.Null(ServerFlavorDetector.ParseExplicit(value));
    }
}
