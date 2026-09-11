using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListPageReuseTests
{
    [Fact]
    public void ReadFarmListsOverview_RefreshesOnceBeforeReadingCurrentValues()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmLists.cs"));

        var methodStart = source.IndexOf("ReadFarmListsOverviewAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task<bool> WaitForFarmListsRenderedAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);

        var method = source[methodStart..methodEnd];
        Assert.Matches(
            @"EnsureRallyPointAndOpenFarmListPageAsync\(\s*cancellationToken,\s*refreshCurrentPage: attempt == 1\)",
            method);
    }

    [Fact]
    public void FarmListRenderWait_RecognizesExplicitZeroCountAsCompletedRender()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var emptyPage = File.ReadAllText(Path.Combine(root, "docs", "DOM", "no_farmlists.txt"));
        Assert.Contains("<span class=\"nominator\">", emptyPage, StringComparison.Ordinal);
        Assert.DoesNotContain("farmListWrapper", emptyPage, StringComparison.Ordinal);

        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmLists.cs"));
        var methodStart = source.IndexOf("WaitForFarmListsRenderedAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task<int?> SendFarmListNowAsync", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.Contains(".farmListCount .nominator", method, StringComparison.Ordinal);
        Assert.Contains("if (confirmedEmpty || rows.Count > 0", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://travian.example/build.php?id=39&gid=16&tt=99")]
    [InlineData("https://travian.example/build.php?tt=99&id=39&gid=16&action=showSlot&lid=7")]
    public void IsOfficialFarmListUrl_AcceptsFarmListPages(string url)
    {
        Assert.True(TravianClient.IsOfficialFarmListUrl(url));
    }

    [Theory]
    [InlineData("https://travian.example/build.php?id=39&gid=16&tt=1")]
    [InlineData("https://travian.example/build.php?id=28&gid=24&tt=99")]
    [InlineData("https://travian.example/dorf1.php")]
    public void IsOfficialFarmListUrl_RejectsOtherPages(string url)
    {
        Assert.False(TravianClient.IsOfficialFarmListUrl(url));
    }
}
