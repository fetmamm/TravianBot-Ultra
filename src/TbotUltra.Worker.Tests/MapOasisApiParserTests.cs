using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class MapOasisApiParserTests
{
    public static TheoryData<string, string, string> OasisTypes => new()
    {
        { "{a:r1} {a.r1} 25%", "Wood 25%", "Wood" },
        { "{a:r1} {a.r1} 50%", "Wood 50%", "Wood" },
        { "{a:r2} {a.r2} 25%", "Clay 25%", "Clay" },
        { "{a:r2} {a.r2} 50%", "Clay 50%", "Clay" },
        { "{a:r3} {a.r3} 25%", "Iron 25%", "Iron" },
        { "{a:r3} {a.r3} 50%", "Iron 50%", "Iron" },
        { "{a:r4} {a.r4} 25%", "Crop 25%", "Crop" },
        { "{a:r4} {a.r4} 50%", "Crop 50%", "Crop" },
        { "{a:r1} {a.r1} 25% {a:r4} {a.r4} 25%", "Wood+Crop", "Wood+Crop" },
        { "{a:r2} {a.r2} 25% {a:r4} {a.r4} 25%", "Clay+Crop", "Clay+Crop" },
        { "{a:r3} {a.r3} 25% {a:r4} {a.r4} 25%", "Iron+Crop", "Iron+Crop" },
    };

    [Theory]
    [MemberData(nameof(OasisTypes))]
    public void Parse_MapsSupportedBonuses(string text, string expectedType, string expectedFilter)
    {
        var json = CreateResponse($"{{\"x\":-12,\"y\":34,\"did\":-1,\"title\":\"{{k.fo}}\",\"text\":\"{text}\"}}");

        var oasis = Assert.Single(MapOasisApiParser.Parse(json));

        Assert.Equal(-12, oasis.X);
        Assert.Equal(34, oasis.Y);
        Assert.False(oasis.IsOccupied);
        Assert.Equal(expectedType, oasis.OasisType);
        Assert.Equal(expectedFilter, oasis.FilterType);
    }

    [Fact]
    public void Parse_IdentifiesOccupiedOasis()
    {
        var json = CreateResponse("{\"x\":1,\"y\":2,\"did\":-1,\"uid\":99,\"title\":\"{k.bt}\",\"text\":\"{a:r1} {a.r1} 25%\"}");

        Assert.True(Assert.Single(MapOasisApiParser.Parse(json)).IsOccupied);
    }

    [Fact]
    public void Parse_ExcludesVillagesEmptyValleysAndUnknownBonuses()
    {
        var json = CreateResponse(
            "{\"x\":1,\"y\":1,\"did\":42,\"title\":\"Village\",\"text\":\"{a:r1} {a.r1} 25%\"}," +
            "{\"x\":2,\"y\":2,\"did\":-1,\"title\":\"{k.vt} {k.f1}\",\"text\":\"{a:r1} {a.r1} 25%\"}," +
            "{\"x\":3,\"y\":3,\"did\":-1,\"title\":\"{k.fo}\",\"text\":\"unknown\"}");

        Assert.Empty(MapOasisApiParser.Parse(json));
    }

    [Fact]
    public void CreateScanCenters_CoversFullFourHundredOneTileWorld()
    {
        var centers = MapOasisApiParser.CreateScanCenters();

        Assert.Equal(169, centers.Count);
        Assert.Equal((-185, -185), centers[0]);
        Assert.Equal((187, 187), centers[^1]);
    }

    private static string CreateResponse(string tiles) => $"{{\"tiles\":[{tiles}]}}";
}
