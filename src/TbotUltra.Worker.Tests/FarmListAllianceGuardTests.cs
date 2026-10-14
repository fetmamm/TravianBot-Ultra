using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListAllianceGuardTests
{
    private static readonly IReadOnlyList<MapSqlVillage> Villages =
    [
        new MapSqlVillage(-119, 116, "Kuwait51", "Kuwait51", null, 577, false, 2456),
        new MapSqlVillage(-122, 137, "marashli village", "marashlı", "SB", 300, false, 3001),
        new MapSqlVillage(-110, 120, "Farm", "Stranger", "XY", 50, false, 4000),
    ];

    private static readonly IReadOnlyList<AllianceMember> Members =
    [
        new AllianceMember(2456, "Kuwait51"),
        new AllianceMember(3001, "marashlı"),
        new AllianceMember(2893, "iceman"),
    ];

    [Fact]
    public void Plan_MatchesMembersByPlayerIdEvenWithoutAllianceInMapSql()
    {
        var plan = FarmListAllianceGuard.Plan(
            [Row("1", -119, 116), Row("2", -122, 137), Row("3", -110, 120)],
            Villages,
            Members);

        Assert.Equal(["1", "2"], plan.Hits.Select(hit => hit.Row.SlotId));
        Assert.Equal("Kuwait51", plan.Hits[0].PlayerName);
        Assert.Equal(3, plan.ActiveRows);
        Assert.Empty(plan.UnknownOwnerRows);
    }

    [Fact]
    public void Plan_SkipsDisabledRowsAndReportsUnknownOwners()
    {
        var plan = FarmListAllianceGuard.Plan(
            [Row("1", -119, 116, disabled: true), Row("4", -1, -1), Row("5", null, null)],
            Villages,
            Members);

        Assert.Empty(plan.Hits);
        Assert.Equal(2, plan.ActiveRows);
        Assert.Equal(["4", "5"], plan.UnknownOwnerRows.Select(row => row.SlotId));
    }

    private static FarmListGuardRow Row(string slotId, int? x, int? y, bool disabled = false) =>
        new(slotId, "Nachbarn", $"target {slotId}", x, y, disabled);
}
