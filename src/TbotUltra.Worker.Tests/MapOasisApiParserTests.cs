using TbotUltra.Worker.Domain;
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
    public void Parse_HandlesBidiControlCharactersInTileText()
    {
        // Travian wraps tile text with Unicode bidi controls (U+202D/U+202C) around the bonus tokens.
        const char lro = '‭';
        const char pdf = '‬';
        var text = $"<br />{{a:r4}}{lro} {{a.r4}}{pdf} 25%<br />{{k.animals}}";
        var json = CreateResponse($"{{\"x\":-38,\"y\":-108,\"did\":-1,\"title\":\"{{k.fo}}\",\"text\":\"{text}\"}}");

        var oasis = Assert.Single(MapOasisApiParser.Parse(json));

        Assert.Equal("Crop 25%", oasis.OasisType);
        Assert.Equal("Crop", oasis.FilterType);
    }

    [Fact]
    public void Parse_ReadsCoordinatesFromNestedPositionObject()
    {
        var json = CreateResponse(
            "{\"position\":{\"x\":-198,\"y\":-170},\"did\":-1,\"title\":\"{k.fo}\",\"text\":\"{a:r1} {a.r1} 25%\"}");

        var oasis = Assert.Single(MapOasisApiParser.Parse(json));

        Assert.Equal(-198, oasis.X);
        Assert.Equal(-170, oasis.Y);
        Assert.Equal("Wood", oasis.FilterType);
    }

    [Fact]
    public void Parse_ReadsCoordinatesFromNumericStrings()
    {
        var json = CreateResponse(
            "{\"x\":\"-198\",\"y\":\"-170\",\"did\":-1,\"title\":\"{k.fo}\",\"text\":\"{a:r1} {a.r1} 25%\"}");

        var oasis = Assert.Single(MapOasisApiParser.Parse(json));

        Assert.Equal(-198, oasis.X);
        Assert.Equal(-170, oasis.Y);
    }

    [Fact]
    public void Parse_ReadsAnimalsForUnoccupiedOasis()
    {
        const string text =
            "{a:r1} {a.r1} 25%<br />{k.animals}<br />" +
            "<div class=\\\"inlineIcon tooltipUnit\\\" title=\\\"\\\"><i class=\\\"unit u35\\\"></i><span class=\\\"value \\\">5</span></div><br />" +
            "<div class=\\\"inlineIcon tooltipUnit\\\" title=\\\"\\\"><i class=\\\"unit u37\\\"></i><span class=\\\"value \\\">4</span></div>";
        var json = CreateResponse($"{{\"x\":1,\"y\":2,\"did\":-1,\"title\":\"{{k.fo}}\",\"text\":\"{text}\"}}");

        var oasis = Assert.Single(MapOasisApiParser.Parse(json));

        Assert.Equal("Wild Boar 5, Bear 4", oasis.Animals);
        Assert.Equal(string.Empty, oasis.OwnerPlayer);
    }

    [Fact]
    public void Parse_ReadsOwnerForOccupiedOasis()
    {
        const string text =
            "{a:r3} {a.r3} 25%<br />{a:r4} {a.r4} 25%<br />" +
            "{k.spieler} SomethingNew<br />{k.allianz} INX<br />{k.volk} {a.v1}";
        var json = CreateResponse($"{{\"x\":1,\"y\":2,\"uid\":1174,\"did\":-1,\"title\":\"{{k.bt}}\",\"text\":\"{text}\"}}");

        var oasis = Assert.Single(MapOasisApiParser.Parse(json));

        Assert.True(oasis.IsOccupied);
        Assert.Equal("SomethingNew", oasis.OwnerPlayer);
        Assert.Equal("INX", oasis.OwnerAlliance);
        Assert.Equal(string.Empty, oasis.Animals);
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
    public void IsRegionOverlay_DetectsRiseOfGovernorsZoomThreeResponse()
    {
        const string json = """
            {"tiles":[
              {"position":{"x":-187,"y":-185},"title":"{k.regionTooltip} Volubilis","text":"The eagles slight eyes VII"},
              {"position":{"x":-186,"y":-185},"did":22071,"title":"{k.regionTooltip} Volubilis","text":"Village in region"}
            ]}
            """;

        Assert.True(MapOasisApiParser.IsRegionOverlay(json));
        Assert.False(MapOasisApiParser.IsRegionOverlay(CreateResponse(
            "{\"position\":{\"x\":-187,\"y\":-185},\"did\":-1,\"title\":\"{k.fo}\",\"text\":\"{a:r2} {a.r2} 25%\"}")));
    }

    [Fact]
    public void CreateScanCenters_CoversFullFourHundredOneTileWorld()
    {
        var centers = MapOasisApiParser.CreateScanCenters();

        Assert.Equal(169, centers.Count);
        Assert.Equal((-185, -185), centers[0]);
        Assert.Equal((187, 187), centers[^1]);
    }

    [Fact]
    public void CreateScanCenters_ForRadiusBounds_VisitsAdjacentRowsInAlternatingDirections()
    {
        var centers = MapOasisApiParser.CreateScanCenters(-30, 30, -30, 30);

        Assert.Equal([(-15, -15), (16, -15), (16, 16), (-15, 16)], centers);
    }

    [Fact]
    public void CreateScanCenters_ForDetailedRegionTiles_UsesBoundedRectangularCoverage()
    {
        var centers = MapOasisApiParser.CreateScanCenters(-198, -178, -192, -176, 10, 8);

        Assert.Equal([(-188, -184)], centers);
    }

    [Fact]
    public void CreateScanCenters_ZoomTwoCoversFullWorldInFourHundredEightyAreas()
    {
        var centers = MapOasisApiParser.CreateScanCenters(-200, 200, -200, 200, 10, 8);

        Assert.Equal(480, centers.Count);
        Assert.Equal((-190, -192), centers[0]);
    }

    [Fact]
    public void CreateScanCenters_StartsNearSelectedVillageAndMovesOutwardWithoutExtraAreas()
    {
        var centers = MapOasisApiParser.CreateScanCenters(
            -40,
            40,
            -40,
            40,
            startingX: 0,
            startingY: 0);

        Assert.Equal(
            [(6, 6), (37, 6), (37, 37), (6, 37), (-25, 37), (-25, 6), (-25, -25), (6, -25), (37, -25)],
            centers);
    }

    [Fact]
    public void CreateScanCenters_WholeMapStartsNearestSelectedVillageWithoutExtraAreas()
    {
        var centers = MapOasisApiParser.CreateScanCenters(startingX: 101, startingY: 114);

        Assert.Equal(169, centers.Count);
        Assert.Equal((94, 125), centers[0]);
        Assert.Equal(169, centers.Distinct().Count());
    }

    [Theory]
    [InlineData(MapOasisScanScope.Radius, 9, 20, 72)]
    [InlineData(MapOasisScanScope.WholeMap, 169, 480, 1665)]
    public void ScanEstimate_MatchesAreaCoverage(
        MapOasisScanScope scope,
        int expectedZoom3,
        int expectedZoom2,
        int expectedZoom1)
    {
        var estimate = MapOasisScanEstimate.Calculate(
            new MapOasisScanRequest(0, 0, scope, MapOasisScanRequest.DefaultRadius, MapOasisScanSpeed.Normal));

        Assert.Equal(expectedZoom3, estimate.Zoom3Requests);
        Assert.Equal(expectedZoom2, estimate.Zoom2Requests);
        Assert.Equal(expectedZoom1, estimate.Zoom1Requests);
    }

    private static string CreateResponse(string tiles) => $"{{\"tiles\":[{tiles}]}}";
}
