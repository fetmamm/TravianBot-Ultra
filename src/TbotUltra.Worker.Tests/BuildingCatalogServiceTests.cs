using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BuildingCatalogServiceTests
{
    [Fact]
    public void TribeCatalog_ContainsExpectedSpecialBuildings()
    {
        var romans = BuildingCatalogService.GetCatalogForTribe("Romans");
        Assert.Contains(romans, item => item.Gid == 31 && item.IsSpecial);
        Assert.Contains(romans, item => item.Gid == 41 && item.IsSpecial);

        var gauls = BuildingCatalogService.GetCatalogForTribe("Gauls");
        Assert.Contains(gauls, item => item.Gid == 33 && item.IsSpecial);
        Assert.Contains(gauls, item => item.Gid == 36 && item.IsSpecial);
    }

    [Fact]
    public void RequirementsFor_KnownBuilding_ReturnsEntries()
    {
        var requirements = BuildingCatalogService.RequirementsFor(17);
        Assert.Contains(requirements, item => item.Name == "Main Building" && item.Level == 3);
    }

    [Fact]
    public void FullCatalog_ContainsSmithyAndTournamentSquare()
    {
        var gauls = BuildingCatalogService.GetFullCatalog("Gauls");
        Assert.Contains(gauls, item => item.Gid == 12 && item.Name == "Smithy");
        Assert.Contains(gauls, item => item.Gid == 14 && item.Name == "Tournament Square");
    }
}
