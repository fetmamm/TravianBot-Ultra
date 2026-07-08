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

    [Theory]
    [InlineData(10, 20)]
    [InlineData(11, 20)]
    [InlineData(23, 10)]
    [InlineData(19, null)]
    public void DuplicateRequiredExistingLevelFor_ReturnsTravianThresholds(int gid, int? expected)
    {
        Assert.Equal(expected, BuildingCatalogService.DuplicateRequiredExistingLevelFor(gid));
    }

    [Theory]
    [InlineData(25, new[] { 26, 44 })]
    [InlineData(26, new[] { 25, 44 })]
    [InlineData(44, new[] { 25, 26 })]
    [InlineData(10, new int[0])]
    public void ResidenceFamilyConflictGidsFor_ReturnsMutualExclusions(int gid, int[] expected)
    {
        Assert.Equal(expected, BuildingCatalogService.ResidenceFamilyConflictGidsFor(gid));
    }

    [Theory]
    [InlineData(25, "Residence")]
    [InlineData(26, "Palace")]
    [InlineData(44, "Command Center")]
    public void NameForGid_ReturnsCatalogName(int gid, string expected)
    {
        Assert.Equal(expected, BuildingCatalogService.NameForGid(gid));
    }

    [Fact]
    public void FullCatalog_ContainsSmithyAndTournamentSquare()
    {
        var gauls = BuildingCatalogService.GetFullCatalog("Gauls");
        Assert.Contains(gauls, item => item.Gid == 13 && item.Name == "Smithy");
        Assert.Contains(gauls, item => item.Gid == 14 && item.Name == "Tournament Square");
    }

    [Fact]
    public void Smithy_IsGid13_WithCostData_AndNoPhantomGid12OrArmoury()
    {
        // The real Travian server (Official) uses gid 13 for Smithy; there is no gid 12 building and no
        // "Armoury". Construct failed before because the catalog mapped Smithy to gid 12 while the server's
        // Construct button was gid 13, so the gid-scoped click refused the "foreign" gid.
        var catalog = BuildingCatalogService.GetFullCatalog("Gauls");

        Assert.Contains(catalog, item => item.Gid == 13 && item.Name == "Smithy");
        Assert.DoesNotContain(catalog, item => item.Gid == 12);
        Assert.DoesNotContain(catalog, item => string.Equals(item.Name, "Armoury", System.StringComparison.OrdinalIgnoreCase));

        // Cost/level data must resolve at gid 13 (the JSON Smithy block moved from key 12 -> 13).
        Assert.True(BuildingCatalogService.MaxLevelFor(13) > 0);
        Assert.NotNull(BuildingCatalogService.CostFor(13, 1));
        Assert.True(BuildingCatalogService.IsSingleInstance(13));
        Assert.Equal(2, BuildingCatalogService.CategoryIndexFor(13));
    }

    [Fact]
    public void CatalogNames_MatchServerDisplayNames()
    {
        // DOM dumps show the server's exact names; the catalog must agree so name-matching works.
        var catalog = BuildingCatalogService.GetFullCatalog("Teutons");
        Assert.Contains(catalog, item => item.Gid == 34 && item.Name == "Stonemason's Lodge");
        Assert.Contains(catalog, item => item.Gid == 37 && item.Name == "Hero's Mansion");
    }

    [Theory]
    [InlineData("Stonemason's Lodge", "Stonemason")]
    [InlineData("Hero's Mansion", "Hero Mansion")]
    [InlineData("Blacksmith", "Smithy")]
    public void SameBuildingName_TreatsServerAndCatalogVariantsAsEqual(string serverName, string catalogName)
    {
        Assert.True(BuildingNames.Same(serverName, catalogName));
    }

    [Fact]
    public void FullCatalog_ContainsHospitalAsArmyBuilding()
    {
        var gauls = BuildingCatalogService.GetFullCatalog("Gauls");

        Assert.Contains(gauls, item => item.Gid == 46 && item.Name == "Hospital" && item.Category == "army_buildings");
        Assert.Equal(2, BuildingCatalogService.CategoryIndexFor(46));
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

    [Fact]
    public void RequirementsFor_Hospital_RequireMainBuildingAndAcademy()
    {
        var requirements = BuildingCatalogService.RequirementsFor(46);

        Assert.Contains(requirements, item => item.Name == "Main Building" && item.Level == 10);
        Assert.Contains(requirements, item => item.Name == "Academy" && item.Level == 15);
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
