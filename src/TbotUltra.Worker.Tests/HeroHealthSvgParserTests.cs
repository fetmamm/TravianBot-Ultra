using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroHealthSvgParserTests
{
    private const string FullPath = "M55 55L47.35 109.46A 55 55 0 0 0 109.87 51.16";

    [Fact]
    public void CalculateHeroHpPercentFromSvgPaths_LiveOfficialPath_Returns62Percent()
    {
        const string maskPath = "M55 55L47.35 109.46A 55 55 0 0 0 100.19 86.36";

        var result = TravianClient.CalculateHeroHpPercentFromSvgPaths(maskPath, FullPath);

        Assert.Equal(62, result);
    }

    [Theory]
    [InlineData("M55 55L47.35 109.46A 55 55 0 0 0 47.35 109.46", 0)]
    [InlineData(FullPath, 100)]
    public void CalculateHeroHpPercentFromSvgPaths_Boundaries_ReturnExpectedPercent(string maskPath, int expected)
    {
        var result = TravianClient.CalculateHeroHpPercentFromSvgPaths(maskPath, FullPath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, FullPath)]
    [InlineData("invalid", FullPath)]
    [InlineData("M56 55L47.35 109.46A 55 55 0 0 0 100.19 86.36", FullPath)]
    public void CalculateHeroHpPercentFromSvgPaths_InvalidContract_ReturnsNull(string? maskPath, string? fullPath)
    {
        Assert.Null(TravianClient.CalculateHeroHpPercentFromSvgPaths(maskPath, fullPath));
    }
}
