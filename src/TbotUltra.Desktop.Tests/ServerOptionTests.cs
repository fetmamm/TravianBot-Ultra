using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ServerOptionTests
{
    [Theory]
    [InlineData("https://elt.ss-travi.com", ServerFlavor.SsTravi, "SS-Travi")]
    [InlineData("https://ts1.travian.com", ServerFlavor.Official, "Official")]
    [InlineData("https://ts3.travian.se", ServerFlavor.Official, "Official")]
    public void ServerFlavorAndLabel_AreDerivedFromBaseUrl(string baseUrl, ServerFlavor expectedFlavor, string expectedLabel)
    {
        var option = new ServerOption { Name = "test", BaseUrl = baseUrl };

        Assert.Equal(expectedFlavor, option.ServerFlavor);
        Assert.Equal(expectedLabel, option.ServerTypeLabel);
    }

    [Fact]
    public void ChangingBaseUrl_RaisesPropertyChangedForDerivedLabel()
    {
        var option = new ServerOption { Name = "test", BaseUrl = "https://elt.ss-travi.com" };
        var changed = new List<string>();
        option.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        option.BaseUrl = "https://ts1.travian.com";

        Assert.Contains(nameof(ServerOption.BaseUrl), changed);
        Assert.Contains(nameof(ServerOption.ServerTypeLabel), changed);
        Assert.Equal("Official", option.ServerTypeLabel);
    }
}
