using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class MainBuildingDurationAnomalyDetectorTests
{
    [Fact]
    public void Detect_FlagsDurationFarAboveHealthyMainBuildingBaseline()
    {
        var expected = BuildingCatalogService.BuildSecondsFor(1, 10, serverSpeed: 3, mainBuildingLevel: 1);

        var anomaly = MainBuildingDurationAnomalyDetector.Detect(
            1,
            10,
            (int)Math.Ceiling(expected * 1.75),
            serverSpeed: 3);

        Assert.NotNull(anomaly);
        Assert.True(anomaly!.Ratio >= 1.5);
    }

    [Fact]
    public void Detect_AcceptsNormalDurationAndNeverBlocksMainBuildingItself()
    {
        var expected = BuildingCatalogService.BuildSecondsFor(1, 10, serverSpeed: 1, mainBuildingLevel: 1);

        Assert.Null(MainBuildingDurationAnomalyDetector.Detect(1, 10, (int)Math.Ceiling(expected * 1.1), 1));
        Assert.Null(MainBuildingDurationAnomalyDetector.Detect(15, 1, int.MaxValue, 1));
    }

    [Theory]
    [InlineData("World 10x", "https://example.com", 10)]
    [InlineData("World", "https://ts1.x3.europe.travian.com", 3)]
    [InlineData("World", "https://example.com", 1)]
    public void ResolveServerSpeed_UsesConfiguredNameOrUrl(string name, string url, double expected)
        => Assert.Equal(expected, MainBuildingDurationAnomalyDetector.ResolveServerSpeed(name, url));
}
