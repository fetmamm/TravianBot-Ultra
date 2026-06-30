using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ServerOptionTests
{
    [Fact]
    public void ToString_ReturnsName()
    {
        var option = new ServerOption { Name = "test", BaseUrl = "https://ts1.travian.com" };

        Assert.Equal("test", option.ToString());
    }

    [Fact]
    public void ChangingBaseUrl_RaisesPropertyChanged()
    {
        var option = new ServerOption { Name = "test", BaseUrl = "https://ts1.travian.com" };
        var changed = new List<string>();
        option.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        option.BaseUrl = "https://ts2.travian.com";

        Assert.Contains(nameof(ServerOption.BaseUrl), changed);
    }
}
