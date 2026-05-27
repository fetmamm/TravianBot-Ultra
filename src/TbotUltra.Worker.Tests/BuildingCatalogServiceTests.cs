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

    [Fact]
    public void RequirementsFor_HorseDrinkingTrough_RequireStableAndRallyPoint()
    {
        var requirements = BuildingCatalogService.RequirementsFor(41);
        Assert.Contains(requirements, item => item.Name == "Stable" && item.Level == 20);
        Assert.Contains(requirements, item => item.Name == "Rally Point" && item.Level == 10);
    }

    [Fact]
    public void RequirementsFor_GreatBarracksAndGreatStable_RequireLevel20BaseBuildings()
    {
        var greatBarracks = BuildingCatalogService.RequirementsFor(29);
        var greatStable = BuildingCatalogService.RequirementsFor(30);

        Assert.Contains(greatBarracks, item => item.Name == "Barracks" && item.Level == 20);
        Assert.Contains(greatStable, item => item.Name == "Stable" && item.Level == 20);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(42)]
    [InlineData(43)]
    public void RequirementsFor_Walls_ReturnsNoRequirements(int gid)
    {
        Assert.Empty(BuildingCatalogService.RequirementsFor(gid));
    }

    [Fact]
    public void CategoryIndexFor_UsesCorrectCategoriesForSpecialCases()
    {
        Assert.Equal(3, BuildingCatalogService.CategoryIndexFor(38));
        Assert.Equal(3, BuildingCatalogService.CategoryIndexFor(39));
        Assert.Equal(1, BuildingCatalogService.CategoryIndexFor(44));
    }
}
