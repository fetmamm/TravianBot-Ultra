namespace TbotUltra.Worker.Services;

internal sealed record AllianceMember(long PlayerId, string PlayerName);

internal sealed record FarmListGuardRow(
    string SlotId,
    string ListName,
    string TargetName,
    int? X,
    int? Y,
    bool Disabled);

internal sealed record FarmListGuardHit(FarmListGuardRow Row, string PlayerName);

internal sealed record FarmListAllianceGuardPlan(
    IReadOnlyList<FarmListGuardHit> Hits,
    int ActiveRows,
    IReadOnlyList<FarmListGuardRow> UnknownOwnerRows);

/// <summary>
/// Finds active farm-list targets owned by members of the own alliance. Ownership comes from map.sql
/// (coordinate to player id); membership comes from the live alliance page because map.sql alliance
/// tags lag behind players who joined since the daily snapshot.
/// </summary>
internal static class FarmListAllianceGuard
{
    public static FarmListAllianceGuardPlan Plan(
        IReadOnlyList<FarmListGuardRow> rows,
        IReadOnlyList<MapSqlVillage> villages,
        IReadOnlyList<AllianceMember> members)
    {
        var memberIds = members.Select(member => member.PlayerId).ToHashSet();
        var villagesByCoordinate = new Dictionary<(int X, int Y), MapSqlVillage>();
        foreach (var village in villages)
        {
            villagesByCoordinate.TryAdd((village.X, village.Y), village);
        }

        var hits = new List<FarmListGuardHit>();
        var unknown = new List<FarmListGuardRow>();
        var activeRows = 0;
        foreach (var row in rows.Where(row => !row.Disabled))
        {
            activeRows++;
            if (row.X is not int x
                || row.Y is not int y
                || !villagesByCoordinate.TryGetValue((x, y), out var village)
                || village.PlayerId is not long playerId)
            {
                // Oases and villages founded after the map.sql snapshot have no known owner.
                unknown.Add(row);
                continue;
            }

            if (memberIds.Contains(playerId))
            {
                hits.Add(new FarmListGuardHit(row, village.PlayerName));
            }
        }

        return new FarmListAllianceGuardPlan(hits, activeRows, unknown);
    }
}
