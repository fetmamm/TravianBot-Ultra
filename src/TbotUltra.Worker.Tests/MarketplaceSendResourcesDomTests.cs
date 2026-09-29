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
        Assert.Equal(
            "root=false send_tab=true send_tab_active=false missing_inputs=lumber,clay,iron,crop",
            MarketplaceSendResourcesDom.DescribeState(html));
    }

    [Fact]
    public void SendResourcesFixture_SatisfiesPageContract()
    {
        var html = ReadFixture("marketplace_sendResources.txt");

        Assert.True(MarketplaceSendResourcesDom.IsReady(html));
        Assert.Equal(
            "/build.php?id=31&gid=17&t=5",
            MarketplaceSendResourcesDom.FindSendResourcesTabHref(html));
        Assert.Equal(
            "root=true send_tab=true send_tab_active=true missing_inputs=none",
            MarketplaceSendResourcesDom.DescribeState(html));
    }

    private static string ReadFixture(string fileName) =>
        TestDomFixtures.Read(fileName);
}
