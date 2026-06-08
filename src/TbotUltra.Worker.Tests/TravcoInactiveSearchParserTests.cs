using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravcoInactiveSearchParserTests
{
    [Fact]
    public void Parse_ReadsLatestPopulationAndCoordinates()
    {
        var raw = new TravcoRawPage(
            2,
            7,
            ["", "Distance", "Travian account", "Village", "07.06.", "06.06."],
            [
                new TravcoRawRow(
                    ["", "12.4", "Player", "Village name", "321", "319"],
                    "https://example.travian.com/karte.php?x=-12&y=34"),
            ]);

        var result = TravcoInactiveSearchParser.Parse(raw);

        var row = Assert.Single(result.Rows);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(7, result.TotalPages);
        Assert.Equal(12.4, row.Distance);
        Assert.Equal("Player", row.Account);
        Assert.Equal("Village name", row.Village);
        Assert.Equal(321, row.Pop);
        Assert.Equal("-12|34", row.Coordinates);
    }

    [Fact]
    public void Parse_AllowsEmptyAndMalformedRows()
    {
        var raw = new TravcoRawPage(
            0,
            0,
            [],
            [
                new TravcoRawRow(["short"], null),
                new TravcoRawRow(["", "-", "Player", "Village (1|2)", "-"], null),
            ]);

        var result = TravcoInactiveSearchParser.Parse(raw);

        var row = Assert.Single(result.Rows);
        Assert.Equal(1, result.PageNumber);
        Assert.Null(row.Distance);
        Assert.Null(row.Pop);
        Assert.Equal("1|2", row.Coordinates);
    }
}
