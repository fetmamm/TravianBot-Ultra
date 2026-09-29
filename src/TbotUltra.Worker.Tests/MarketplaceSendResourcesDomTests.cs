using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class MarketplaceSendResourcesDomTests
{
    [Fact]
    public void ManagementFixture_RequiresSendResourcesTabSwitch()
    {
        var html = ReadFixture("marketplace_management.txt");

        Assert.False(MarketplaceSendResourcesDom.IsReady(html));
        Assert.Equal(
            "/build.php?id=31&gid=17&t=5",
            MarketplaceSendResourcesDom.FindSendResourcesTabHref(html));
    }

    [Fact]
    public void SendResourcesFixture_SatisfiesPageContract()
    {
        var html = ReadFixture("marketplace_sendResources.txt");

        Assert.True(MarketplaceSendResourcesDom.IsReady(html));
        Assert.Equal(
            "/build.php?id=31&gid=17&t=5",
            MarketplaceSendResourcesDom.FindSendResourcesTabHref(html));
    }

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "docs",
            "DOM",
            fileName));
}
