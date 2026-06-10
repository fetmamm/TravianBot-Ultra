using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class MapOasisParserTests
{
    public static TheoryData<int, string> LandscapeMappings => new()
    {
        { 10, "Wood 25%" },
        { 11, "Wood 25%" },
        { 40, "Wood 50%" },
        { 41, "Wood 50%" },
        { 12, "Clay 25%" },
        { 13, "Clay 25%" },
        { 42, "Clay 50%" },
        { 43, "Clay 50%" },
        { 14, "Iron 25%" },
        { 15, "Iron 25%" },
        { 44, "Iron 50%" },
        { 45, "Iron 50%" },
        { 16, "Crop 25%" },
        { 17, "Crop 25%" },
        { 46, "Crop 50%" },
        { 47, "Crop 50%" },
        { 20, "Wood+Crop" },
        { 21, "Wood+Crop" },
        { 22, "Clay+Crop" },
        { 23, "Clay+Crop" },
        { 24, "Iron+Crop" },
        { 25, "Iron+Crop" },
    };

    [Theory]
    [MemberData(nameof(LandscapeMappings))]
    public void TryMapLandscape_MapsSupportedLandscapes(int landscape, string expected)
    {
        Assert.True(MapOasisParser.TryMapLandscape(landscape, out var oasisType, out _));
        Assert.Equal(expected, oasisType);
    }

    [Fact]
    public void Parse_FiltersRowsTypesOccupiedAndUnknownLandscapes()
    {
        string[] lines =
        [
            "INSERT INTO `x_world` VALUES (-1, 2, 10, 3, 0, 'Free oasis', 0, '', 0);",
            "INSERT INTO `x_world` VALUES (3, 4, 40, 3, 0, 'Occupied oasis', 99, 'Player', 0);",
            "INSERT INTO `x_world` VALUES (5, 6, 12, 3, 0, 'Clay oasis', 0, '', 0);",
            "INSERT INTO `x_world` VALUES (7, 8, 10, 1, 1, 'Village', 5, 'Player', 0);",
            "INSERT INTO `x_world` VALUES (9, 10, 99, 3, 0, 'Unknown', 0, '', 0);",
        ];

        var freeWood = MapOasisParser.Parse(lines, false, ["Wood"]);
        var allWood = MapOasisParser.Parse(lines, true, ["Wood"]);

        Assert.Single(freeWood);
        Assert.Equal("-1|2", $"{freeWood[0].X}|{freeWood[0].Y}");
        Assert.Equal(2, allWood.Count);
        Assert.Contains(allWood, oasis => oasis.IsOccupied);
    }

    [Fact]
    public void Parse_HandlesQuotedCommasAndEscapedApostrophes()
    {
        string[] lines =
        [
            "INSERT INTO `x_world` VALUES (-7, -8, 44, 3, 0, 'King\\'s oasis, west', 0, '', 0);",
            "INSERT INTO `x_world` VALUES (1, 2, 20, 3, 0, 'O''Brien, east', 0, '', 0);",
            "INSERT INTO `x_world` VALUES (broken);",
            "not sql",
        ];

        var result = MapOasisParser.Parse(lines, false, ["Iron", "Wood+Crop"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("Iron 50%", result[0].OasisType);
        Assert.Equal("Wood+Crop", result[1].OasisType);
    }

    [Fact]
    public void DetectSchema_RecognizesOfficialVillageOnlyFormat()
    {
        string[] lines =
        [
            "INSERT INTO `x_world` VALUES (82,-119,200,1,36264,'03',1519,'Player',181,'Alliance',630,NULL,FALSE,NULL,NULL,NULL);",
        ];

        Assert.Equal(
            MapOasisParser.MapSqlSchema.OfficialVillages,
            MapOasisParser.DetectSchema(lines));
    }

    [Fact]
    public void DetectSchema_RecognizesLandscapeOasisFormat()
    {
        string[] lines =
        [
            "INSERT INTO `x_world` VALUES (-1,2,10,3,0,'Free oasis',0,'',0);",
        ];

        Assert.Equal(
            MapOasisParser.MapSqlSchema.OasisLandscape,
            MapOasisParser.DetectSchema(lines));
    }
}
