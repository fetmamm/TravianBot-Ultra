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
    public void ParseVillages_ReadsOfficialCoordinatesAndFiltersSelectedSources()
    {
        const string mapSql = """
            INSERT INTO `x_world` VALUES
            (22028,173,146,5,31912,'Natars 173|146',1,'Natars',0,"",498,NULL,FALSE,NULL,NULL,NULL),
            (22029,-4,12,1,31913,'Player village',2,'Alice',3,'ALLY',99,NULL,FALSE,NULL,NULL,NULL),
            (22030,5,9,1,31914,'Hunter village',3,'Multihunter',0,"",1,NULL,FALSE,NULL,NULL,NULL);
            """;

        var villages = MapSqlPlayerParser.ParseVillages(mapSql);
        var natars = MapSqlPlayerParser.FilterVillages(villages, includePlayers: false, includeNatars: true, [], []);
        var players = MapSqlPlayerParser.FilterVillages(villages, includePlayers: true, includeNatars: false, [], []);

        var natar = Assert.Single(natars);
        Assert.Equal(173, natar.X);
        Assert.Equal(146, natar.Y);
        Assert.Equal("Natars 173|146", natar.VillageName);
        Assert.True(natar.IsNatar);
        var player = Assert.Single(players);
        Assert.Equal("Alice", player.PlayerName);
        Assert.Equal("ALLY", player.Alliance);
        Assert.Equal(2, player.PlayerId);
    }

    [Fact]
    public void FilterVillages_ExcludesIgnoredPlayerAndAlliance()
    {
        var villages = new[]
        {
            new MapSqlVillage(1, 2, "A", "Alice", "ALLY", 10, false),
            new MapSqlVillage(3, 4, "B", "Bob", "BETA", 20, false),
        };

        var result = MapSqlPlayerParser.FilterVillages(villages, true, true, [" alice "], ["beta"]);

        Assert.Empty(result);
    }

    [Fact]
    public void FilterVillages_ExcludesEveryVillageOwnedByCurrentPlayer()
    {
        var villages = new[]
        {
            new MapSqlVillage(1, 2, "Own A", "Swollen", "ALLY", 10, false),
            new MapSqlVillage(3, 4, "Own B", " swollen ", "ALLY", 20, false),
            new MapSqlVillage(5, 6, "Other", "Alice", "ALLY", 30, false),
        };

        var result = MapSqlPlayerParser.FilterVillages(villages, true, true, ["SWOLLEN"], []);

        var remaining = Assert.Single(result);
        Assert.Equal("Alice", remaining.PlayerName);
    }

    [Fact]
    public void TryExtractBulkMessageMissingPlayerName_ReadsOfficialDialogText()
    {
        var name = TravianClient.TryExtractBulkMessageMissingPlayerName("The name grezullallala does not exist.");

        Assert.Equal("grezullallala", name);
    }
}
