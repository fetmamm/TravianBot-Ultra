using System.Globalization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal sealed record MapSqlVillagePlayer(
    string PlayerName,
    string? Alliance,
    long Population);

internal static class MapSqlPlayerParser
{
    public static IReadOnlyList<MapSqlVillagePlayer> Parse(string mapSql)
    {
        if (string.IsNullOrWhiteSpace(mapSql))
        {
            return [];
        }

        var rows = new List<MapSqlVillagePlayer>();
        var foundValuesBlock = false;
        var searchIndex = 0;
        while (TryFindValuesBlock(mapSql, searchIndex, out var valuesIndex))
        {
            foundValuesBlock = true;
            ParseTuplesUntilStatementEnd(mapSql, valuesIndex, rows, out searchIndex);
        }

        if (!foundValuesBlock)
        {
            ParseTuplesUntilStatementEnd(mapSql, 0, rows, out _);
        }

        return rows;
    }

    public static BulkMessageAnalyzeResult Analyze(
        IReadOnlyList<MapSqlVillagePlayer> villagePlayers,
        IReadOnlyCollection<string> sentPlayers,
        IReadOnlyCollection<string> excludedPlayers,
        IReadOnlyCollection<string> excludedAlliances,
        BulkMessageSortOrder sortOrder)
    {
        var sentKeys = ToKeySet(sentPlayers);
        var excludedPlayerKeys = ToKeySet(excludedPlayers);
        var excludedAllianceKeys = ToKeySet(excludedAlliances);

        var aggregates = villagePlayers
            .Where(row => !string.IsNullOrWhiteSpace(row.PlayerName))
            .GroupBy(row => NormalizeNameKey(row.PlayerName), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var alliances = group
                    .Select(item => item.Alliance)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new PlayerAggregate(
                    Key: group.Key,
                    Name: first.PlayerName.Trim(),
                    Alliance: alliances.FirstOrDefault(),
                    AllianceKeys: alliances.Select(NormalizeNameKey).ToHashSet(StringComparer.Ordinal),
                    Population: group.Sum(item => Math.Max(0, item.Population)),
                    VillageCount: group.Count());
            })
            .ToList();

        var playersAnalyzed = aggregates.Count;
        var sentCachedCount = aggregates.Count(player => sentKeys.Contains(player.Key));

        var eligible = aggregates
            .Where(player => !sentKeys.Contains(player.Key))
            .Where(player => !excludedPlayerKeys.Contains(player.Key))
            .Where(player => !player.AllianceKeys.Overlaps(excludedAllianceKeys))
            .Where(player => !string.Equals(player.Name, "Multihunter", StringComparison.OrdinalIgnoreCase))
            .Select(player => new BulkMessagePlayer(
                player.Name,
                player.Alliance,
                player.Population,
                player.VillageCount));

        eligible = sortOrder == BulkMessageSortOrder.PopulationAscending
            ? eligible.OrderBy(player => player.Population).ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            : eligible.OrderByDescending(player => player.Population).ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase);

        var players = eligible.ToList();
        return new BulkMessageAnalyzeResult(
            PlayersAnalyzed: playersAnalyzed,
            EligiblePlayers: players.Count,
            SentCachedCount: sentCachedCount,
            Players: players);
    }

    public static string NormalizeNameKey(string? value)
    {
        return string.Join(
            " ",
            (value ?? string.Empty)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private static HashSet<string> ToKeySet(IEnumerable<string> values)
    {
        return values
            .Select(NormalizeNameKey)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool TryFindValuesBlock(string text, int startIndex, out int valuesIndex)
    {
        valuesIndex = text.IndexOf("VALUES", startIndex, StringComparison.OrdinalIgnoreCase);
        if (valuesIndex < 0)
        {
            return false;
        }

        valuesIndex += "VALUES".Length;
        return true;
    }

    private static void ParseTuplesUntilStatementEnd(
        string text,
        int startIndex,
        List<MapSqlVillagePlayer> rows,
        out int nextIndex)
    {
        nextIndex = text.Length;
        var index = Math.Max(0, startIndex);
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == ';')
            {
                nextIndex = index + 1;
                return;
            }

            if (ch != '(')
            {
                index++;
                continue;
            }

            if (TryParseTuple(text, index, out var fields, out var afterTuple))
            {
                if (TryCreatePlayer(fields, out var player))
                {
                    rows.Add(player);
                }

                index = afterTuple;
                continue;
            }

            index++;
        }
    }

    private static bool TryParseTuple(string text, int startIndex, out List<string> fields, out int nextIndex)
    {
        fields = [];
        nextIndex = startIndex + 1;
        if (startIndex < 0 || startIndex >= text.Length || text[startIndex] != '(')
        {
            return false;
        }

        var current = new System.Text.StringBuilder();
        var inQuote = false;
        for (var index = startIndex + 1; index < text.Length; index++)
        {
            var ch = text[index];
            if (inQuote)
            {
                if (ch == '\\' && index + 1 < text.Length)
                {
                    current.Append(text[index + 1]);
                    index++;
                    continue;
                }

                if (ch == '\'')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        current.Append('\'');
                        index++;
                        continue;
                    }

                    inQuote = false;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch == '\'')
            {
                inQuote = true;
                continue;
            }

            if (ch == ',')
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            if (ch == ')')
            {
                fields.Add(current.ToString().Trim());
                nextIndex = index + 1;
                return true;
            }

            current.Append(ch);
        }

        return false;
    }

    private static bool TryCreatePlayer(IReadOnlyList<string> fields, out MapSqlVillagePlayer player)
    {
        player = new MapSqlVillagePlayer(string.Empty, null, 0);
        if (fields.Count < 11)
        {
            return false;
        }

        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        // Official map.sql/x_world:
        // x, y, tribe, villageId, villageName, playerId, playerName, allianceId, allianceName, population, ...
        var playerName = CleanSqlValue(fields[7]);
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        var alliance = CleanSqlValue(fields[9]);
        _ = long.TryParse(fields[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var population);
        player = new MapSqlVillagePlayer(
            playerName.Trim(),
            string.IsNullOrWhiteSpace(alliance) ? null : alliance.Trim(),
            population);
        return true;
    }

    private static string CleanSqlValue(string value)
    {
        return string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value.Trim();
    }

    private sealed record PlayerAggregate(
        string Key,
        string Name,
        string? Alliance,
        HashSet<string> AllianceKeys,
        long Population,
        int VillageCount);
}
