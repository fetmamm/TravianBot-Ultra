using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class OfficialFarmSelectionTests
{
    [Fact]
    public void Filter_AppliesPopulationDistanceOrderAndLimit()
    {
        var rows = new[]
        {
            Row("1|1", pop: 20, distance: 5),
            Row("2|2", pop: 40, distance: 3),
            Row("3|3", pop: 60, distance: 2),
        };

        var result = OfficialFarmSelection.Filter(
            rows,
            new HashSet<string>(),
            amount: 2,
            order: "distance_asc",
            populationMode: "under",
            populationLimit: 50,
            maximumDistance: 5,
            skipDuplicates: true);

        Assert.Equal([(2, 2), (1, 1)], result.Select(item => (item.X, item.Y)).ToArray());
    }

    [Fact]
    public void Filter_SkipsExistingAndSourceDuplicates()
    {
        var rows = new[]
        {
            Row("1|1", pop: 10, distance: 1),
            Row("[1|1]", pop: 10, distance: 1),
            Row("2|2", pop: 20, distance: 2),
        };

        var result = OfficialFarmSelection.Filter(
            rows,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1|1" },
            amount: 10,
            order: "pop_desc",
            populationMode: "all",
            populationLimit: 0,
            maximumDistance: null,
            skipDuplicates: true);

        var coordinate = Assert.Single(result);
        Assert.Equal((2, 2), (coordinate.X, coordinate.Y));
    }

    [Fact]
    public void Filter_DoesNotApplyATotalRunLimit()
    {
        var rows = Enumerable.Range(0, 1201)
            .Select(index => Row($"{index}|{index}", pop: index, distance: index))
            .ToArray();

        var result = OfficialFarmSelection.Filter(
            rows,
            new HashSet<string>(),
            amount: 1201,
            order: "distance_asc",
            populationMode: "all",
            populationLimit: 0,
            maximumDistance: null,
            skipDuplicates: true);

        Assert.Equal(1201, result.Count);
    }

    [Fact]
    public void FarmListStatusRow_ShowsOfficialCapacity()
    {
        var row = new FarmListStatusRow
        {
            TotalFarmCount = 5,
            Capacity = 100,
        };

        Assert.Equal("5/100 farms", row.FarmCountText);
    }

    private static TravcoListStore.TravcoSavedRow Row(string coordinates, long pop, double distance) =>
        new()
        {
            Coordinates = coordinates,
            Pop = pop,
            Distance = distance,
            Selected = true,
        };
}
