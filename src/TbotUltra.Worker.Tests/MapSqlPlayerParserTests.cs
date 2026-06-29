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
            (1,2,1,100,'Village One',10,'Alice',1,'ALLY',123,0,0,0,0),
            (3,4,2,101,'Village Two',11,'Bob',2,'BETA',456,0,0,0,0);
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
            (-1,2,1,100,'Village, One',10,'Alice''s Name',1,'A\'Team',123,0,0,0,0);
            """;

        var row = Assert.Single(MapSqlPlayerParser.Parse(mapSql));

        Assert.Equal("Alice's Name", row.PlayerName);
        Assert.Equal("A'Team", row.Alliance);
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
    public void Analyze_FiltersSentExcludedAllianceAndMultihunter()
    {
        var rows = new[]
        {
            new MapSqlVillagePlayer("Alice", "ALLY", 100),
            new MapSqlVillagePlayer("Bob", "BETA", 200),
            new MapSqlVillagePlayer("Charlie", "NOPE", 300),
            new MapSqlVillagePlayer("Multihunter", null, 400),
        };

        var result = MapSqlPlayerParser.Analyze(
            rows,
            sentPlayers: ["Alice"],
            excludedPlayers: ["Bob"],
            excludedAlliances: ["NOPE"],
            BulkMessageSortOrder.PopulationDescending);

        Assert.Empty(result.Players);
        Assert.Equal(4, result.PlayersAnalyzed);
        Assert.Equal(1, result.SentCachedCount);
    }
}
