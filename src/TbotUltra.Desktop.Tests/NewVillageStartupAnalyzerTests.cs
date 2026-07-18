using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class NewVillageStartupAnalyzerTests
{
    [Fact]
    public void FindVillagesWithoutKnownStatus_ReturnsMissingAndIncompleteVillages()
    {
        var villages = new[]
        {
            new Village("Capital", "dorf1.php?newdid=1"),
            new Village("Known", "dorf1.php?newdid=2"),
            new Village("New village", "dorf1.php?newdid=3"),
        };
        var cache = new Dictionary<string, VillageStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["Capital"] = CreateStatus("Capital", hasFields: true, hasBuildings: true),
            ["Known"] = CreateStatus("Known", hasFields: true, hasBuildings: false),
        };

        var result = NewVillageStartupAnalyzer.FindVillagesWithoutKnownStatus(villages, cache);

        Assert.Equal(new[] { "Known", "New village" }, result.Select(village => village.Name));
    }

    [Fact]
    public void FindVillagesWithoutKnownStatus_MatchesNamesCaseInsensitively()
    {
        var villages = new[] { new Village(" GREZ ", "dorf1.php?newdid=1") };
        var cache = new Dictionary<string, VillageStatus>
        {
            ["grez"] = CreateStatus("GREZ", hasFields: true, hasBuildings: true),
        };

        Assert.Empty(NewVillageStartupAnalyzer.FindVillagesWithoutKnownStatus(villages, cache));
    }

    [Fact]
    public void FindVillagesWithoutKnownStatus_DuplicateNameUsesCoordinates()
    {
        var villages = new[]
        {
            new Village("New village", "dorf1.php?newdid=28803", CoordX: 93, CoordY: -17),
            new Village("New village", "dorf1.php?newdid=28805", CoordX: 93, CoordY: -19),
        };
        var cache = new Dictionary<string, VillageStatus>
        {
            ["xy:93|-17"] = CreateStatus("New village", hasFields: true, hasBuildings: true),
        };

        var result = NewVillageStartupAnalyzer.FindVillagesWithoutKnownStatus(villages, cache);

        var missing = Assert.Single(result);
        Assert.Equal("dorf1.php?newdid=28805", missing.Url);
        Assert.Equal(-19, missing.CoordY);
    }

    private static VillageStatus CreateStatus(string name, bool hasFields, bool hasBuildings)
    {
        return new VillageStatus(
            ActiveVillage: name,
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: hasFields ? [new ResourceField(1, "wood", "Woodcutter", 1, "build.php?id=1")] : [],
            Buildings: hasBuildings ? [new Building(26, "Main Building", 1, "build.php?id=26", 15)] : [],
            BuildQueue: []);
    }
}
