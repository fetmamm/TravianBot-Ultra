using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class MapSqlPlayerParserTests
{
    [Fact]
    public void Parse_ReadsPlayersFromInsertTuples()
    {
        const string mapSql = """
            INSERT INTO `x_world` VALUES
            (1,2,1,100,'Village One',10,1001,'Alice',1,'ALLY',123,NULL,FALSE,NULL,NULL,NULL),
            (3,4,2,101,'Village Two',11,1002,'Bob',2,'BETA',456,NULL,FALSE,NULL,NULL,NULL);
            """;

        var rows = MapSqlPlayerParser.Parse(mapSql);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", rows[0].PlayerName);
        Assert.Equal("ALLY", rows[0].Alliance);
        Assert.Equal(123, rows[0].Population);
        Assert.Equal("Bob", rows[1].PlayerName);
    }

    [Fact]
    public void Parse_HandlesEscapedQuotesAndCommas()
    {
        const string mapSql = """
            INSERT INTO `x_world` VALUES
            (-1,2,1,100,'Village, One',10,1001,'Alice''s Name',1,'A\'Team',123,NULL,FALSE,NULL,NULL,NULL);
            """;

        var row = Assert.Single(MapSqlPlayerParser.Parse(mapSql));

        Assert.Equal("Alice's Name", row.PlayerName);
        Assert.Equal("A'Team", row.Alliance);
    }

    [Fact]
    public void Parse_DoesNotUsePlayerIdAsName()
    {
        const string mapSql = """
            INSERT INTO `x_world` VALUES (82,-119,200,1,36264,'03',1519,'Bam Bamm',181,'Türk',630,NULL,FALSE,NULL,NULL,NULL);
            """;

        var row = Assert.Single(MapSqlPlayerParser.Parse(mapSql));

        Assert.Equal("Bam Bamm", row.PlayerName);
        Assert.Equal("Türk", row.Alliance);
        Assert.Equal(630, row.Population);
    }

    [Fact]
    public void Analyze_AggregatesPopulationAndSortsDescendingByDefault()
    {
        var rows = new[]
        {
            new MapSqlVillagePlayer("Alice", "ALLY", 100),
            new MapSqlVillagePlayer("Bob", "BETA", 300),
            new MapSqlVillagePlayer("Alice", "ALLY", 250),
        };

        var result = MapSqlPlayerParser.Analyze(rows, [], [], [], BulkMessageSortOrder.PopulationDescending);

        Assert.Equal(2, result.PlayersAnalyzed);
        Assert.Equal(["Alice", "Bob"], result.Players.Select(player => player.Name).ToArray());
        Assert.Equal(350, result.Players[0].Population);
        Assert.Equal(2, result.Players[0].VillageCount);
    }

    [Fact]
    public void Analyze_CanSortAscending()
    {
        var rows = new[]
        {
            new MapSqlVillagePlayer("Alice", "ALLY", 100),
            new MapSqlVillagePlayer("Bob", "BETA", 300),
        };

        var result = MapSqlPlayerParser.Analyze(rows, [], [], [], BulkMessageSortOrder.PopulationAscending);

        Assert.Equal(["Alice", "Bob"], result.Players.Select(player => player.Name).ToArray());
    }

    [Fact]
    public void Analyze_FiltersSentExcludedAllianceAndSystemPlayers()
    {
        var rows = new[]
        {
            new MapSqlVillagePlayer("Alice", "ALLY", 100),
            new MapSqlVillagePlayer("Bob", "BETA", 200),
            new MapSqlVillagePlayer("Charlie", "NOPE", 300),
            new MapSqlVillagePlayer("Multihunter", null, 400),
            new MapSqlVillagePlayer("Natars", null, 500),
        };

        var result = MapSqlPlayerParser.Analyze(
            rows,
            sentPlayers: ["Alice"],
            excludedPlayers: ["Bob"],
            excludedAlliances: ["NOPE"],
            BulkMessageSortOrder.PopulationDescending);

        Assert.Empty(result.Players);
        Assert.Equal(5, result.PlayersAnalyzed);
        Assert.Equal(1, result.SentCachedCount);
    }

    [Theory]
    [InlineData("Multihunter")]
    [InlineData(" Natars ")]
    [InlineData("Natar")]
    public void IsProtectedPlayerName_BlocksSystemPlayers(string playerName)
    {
        Assert.True(MapSqlPlayerParser.IsProtectedPlayerName(playerName));
    }

    [Fact]
    public void TryExtractBulkMessageMissingPlayerName_ReadsOfficialDialogText()
    {
        var name = TravianClient.TryExtractBulkMessageMissingPlayerName("The name grezullallala does not exist.");

        Assert.Equal("grezullallala", name);
    }
}
