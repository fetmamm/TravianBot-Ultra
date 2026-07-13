using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class IdleBrowsePageSelectionTests
{
    [Fact]
    public void GetEnabledIdleBrowsePages_IncludesOfficialStatisticsPagesByDefault()
    {
        var pages = MainWindow.GetEnabledIdleBrowsePages(new BotOptions());

        Assert.Contains("/statistics/general", pages);
        Assert.Contains("/statistics/hero", pages);
        Assert.Contains("/statistics/player/top10", pages);
        Assert.Contains("/statistics/player/defenders", pages);
        Assert.Contains("/statistics/player/attackers", pages);
    }

    [Theory]
    [InlineData("/statistics/hero")]
    [InlineData("/statistics/player/top10")]
    [InlineData("/statistics/player/defenders")]
    [InlineData("/statistics/player/attackers")]
    [InlineData("/statistics/general")]
    public void RequiresStatisticsLandingPage_ReturnsTrueForStatisticsSubPages(string page)
    {
        Assert.True(MainWindow.RequiresStatisticsLandingPage(page));
    }

    [Theory]
    [InlineData("/statistics")]
    [InlineData("karte.php")]
    public void RequiresStatisticsLandingPage_ReturnsFalseForLandingAndOtherPages(string page)
    {
        Assert.False(MainWindow.RequiresStatisticsLandingPage(page));
    }
}
