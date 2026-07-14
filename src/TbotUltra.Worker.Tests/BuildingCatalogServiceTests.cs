using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BuildingCatalogServiceTests
{
    private static readonly int[] CatalogGids =
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
        31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
    ];

    [Fact]
    public void EmbeddedCatalog_LoadsWithoutExternalConfigFile()
    {
        using var stream = typeof(BuildingCatalogService).Assembly
            .GetManifestResourceStream(BuildingCatalogService.CatalogResourceName);

        Assert.NotNull(stream);
        Assert.Null(BuildingCatalogService.CatalogLoadError);
    }

    [Fact]
    public void Catalog_AllSupportedBuildingsHaveContiguousLevelData()
    {
        foreach (var gid in CatalogGids)
        {
            var maxLevel = BuildingCatalogService.MaxLevelFor(gid);
            var levels = BuildingCatalogService.LevelsFor(gid);

            Assert.NotNull(levels);
            Assert.Equal(maxLevel, levels.Count);
            Assert.Equal(Enumerable.Range(1, maxLevel), levels.Select(level => level.Level));
            Assert.All(levels, level => Assert.True(level.BuildSeconds1x > 0, $"gid {gid} level {level.Level}"));
        }

        Assert.Null(BuildingCatalogService.LevelsFor(12));
        Assert.Null(BuildingCatalogService.LevelsFor(50));
    }

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

    [Theory]
    [InlineData("Romans", 31, 41)]
    [InlineData("Teutons", 32, 35)]
    [InlineData("Gauls", 33, 36)]
    [InlineData("Egyptians", 42, 45)]
    [InlineData("Huns", 43, 44)]
    [InlineData("Spartans", 47, 48)]
    public void TribeCatalog_ContainsOnlyTheExpectedTribeSpecials(string tribe, int wallOrFirst, int second)
    {
        var catalog = BuildingCatalogService.GetCatalogForTribe(tribe);

        Assert.Contains(catalog, item => item.Gid == wallOrFirst && item.IsSpecial);
        Assert.Contains(catalog, item => item.Gid == second && item.IsSpecial);
        Assert.Equal(2, catalog.Count(item => item.IsSpecial));
    }

    [Fact]
    public void Spartans_UseAsclepeionInsteadOfHospital()
    {
        var catalog = BuildingCatalogService.GetCatalogForTribe("Spartans");

        Assert.Contains(catalog, item => item.Gid == 48 && item.Name == "Asclepeion");
        Assert.DoesNotContain(catalog, item => item.Gid == 46);
    }

    [Theory]
    [InlineData("Romans", 31)]
    [InlineData("Teutons", 32)]
    [InlineData("Gauls", 33)]
    [InlineData("Egyptians", 42)]
    [InlineData("Huns", 43)]
    [InlineData("Spartans", 47)]
    public void WallForTribe_ReturnsOfficialWallGid(string tribe, int expectedGid)
    {
        Assert.Equal(expectedGid, BuildingCatalogService.WallForTribe(tribe)?.Gid);
    }

    [Theory]
    [InlineData(1, 3, 110, 280, 140, 165)]
    [InlineData(4, 3, 195, 250, 195, 55)]
    [InlineData(10, 4, 275, 335, 190, 85)]
    [InlineData(11, 5, 215, 270, 190, 55)]
    [InlineData(35, 1, 3210, 2050, 2750, 3830)]
    [InlineData(44, 1, 1600, 1250, 1050, 200)]
    [InlineData(45, 1, 910, 945, 910, 340)]
    [InlineData(47, 1, 160, 100, 80, 60)]
    [InlineData(48, 1, 320, 280, 420, 360)]
    public void Catalog_OfficialAnchorCostsMatch(
        int gid,
        int level,
        int wood,
        int clay,
        int iron,
        int crop)
    {
        var stats = BuildingCatalogService.CostFor(gid, level);

        Assert.NotNull(stats);
        Assert.Equal((wood, clay, iron, crop), (stats.Wood, stats.Clay, stats.Iron, stats.Crop));
    }

    [Fact]
    public void Brewery_UsesCurrentTwentyLevelCatalog()
    {
        Assert.Equal(20, BuildingCatalogService.MaxLevelFor(35));
        Assert.Equal(191215, BuildingCatalogService.CostFor(35, 20)?.Wood);
    }

    [Fact]
    public void Harbor_HasEstimateDataButIsNotOfferedWithoutShoreContext()
    {
        Assert.Equal(49, BuildingCatalogService.GidForName("Harbor"));
        Assert.Equal(1, BuildingCatalogService.CategoryIndexFor(49));
        Assert.NotNull(BuildingCatalogService.CostFor(49, 20));
        Assert.DoesNotContain(BuildingCatalogService.GetCatalogForTribe("Romans"), item => item.Gid == 49);
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

    [Fact]
    public void RequirementsFor_TribeBuildings_MatchOfficialRules()
    {
        Assert.Contains(BuildingCatalogService.RequirementsFor(44), item => item.Name == "Main Building" && item.Level == 5);
        Assert.Contains(BuildingCatalogService.RequirementsFor(45), item => item.Name == "Hero's Mansion" && item.Level == 10);
        Assert.Contains(BuildingCatalogService.RequirementsFor(48), item => item.Name == "Main Building" && item.Level == 5);
        Assert.Contains(BuildingCatalogService.RequirementsFor(48), item => item.Name == "Academy" && item.Level == 10);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(47)]
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
