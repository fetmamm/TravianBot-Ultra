using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TroopCatalogTests
{
    [Fact]
    public void ResolveTroopTypesForTribeAndBuilding_SplitsRomanTroopsByBuilding()
    {
        Assert.Equal(
            ["Legionnaire", "Praetorian", "Imperian"],
            TroopCatalog.ResolveTroopTypesForTribe("Romans", TroopTrainingBuildingType.Barracks));
        Assert.Equal(
            ["Equites Legati", "Equites Imperatoris", "Equites Caesaris"],
            TroopCatalog.ResolveTroopTypesForTribe("Romans", TroopTrainingBuildingType.Stable));
        Assert.Equal(
            ["Ram", "Fire Catapult"],
            TroopCatalog.ResolveTroopTypesForTribe("Romans", TroopTrainingBuildingType.Workshop));
    }

    [Fact]
    public void ResolveTroopTypesForTribeAndBuilding_SplitsTeutonTroopsWithScoutInBarracks()
    {
        Assert.Equal(
            ["Clubswinger", "Spearman", "Axeman", "Scout"],
            TroopCatalog.ResolveTroopTypesForTribe("Teutons", TroopTrainingBuildingType.Barracks));
        Assert.Equal(
            ["Paladin", "Teutonic Knight"],
            TroopCatalog.ResolveTroopTypesForTribe("Teutons", TroopTrainingBuildingType.Stable));
        Assert.Equal(
            ["Ram", "Catapult"],
            TroopCatalog.ResolveTroopTypesForTribe("Teutons", TroopTrainingBuildingType.Workshop));
    }

    [Fact]
    public void ResolveTroopTypesForTribeAndBuilding_SplitsGaulTroopsWithPathfinderInStable()
    {
        Assert.Equal(
            ["Phalanx", "Swordsman"],
            TroopCatalog.ResolveTroopTypesForTribe("Gauls", TroopTrainingBuildingType.Barracks));
        Assert.Equal(
            ["Pathfinder", "Theutates Thunder", "Druidrider", "Haeduan"],
            TroopCatalog.ResolveTroopTypesForTribe("Gauls", TroopTrainingBuildingType.Stable));
        Assert.Equal(
            ["Ram", "Trebuchet"],
            TroopCatalog.ResolveTroopTypesForTribe("Gauls", TroopTrainingBuildingType.Workshop));
    }

    [Theory]
    [InlineData("Romans", "Legionnaire", TroopTrainingBuildingType.Barracks, true)]
    [InlineData("Romans", "Equites Legati", TroopTrainingBuildingType.Barracks, false)]
    [InlineData("Gauls", "Pathfinder", TroopTrainingBuildingType.Barracks, false)]
    [InlineData("Gauls", "Pathfinder", TroopTrainingBuildingType.Stable, true)]
    [InlineData("Teutons", "Scout", TroopTrainingBuildingType.Barracks, true)]
    [InlineData("Teutons", "Scout", TroopTrainingBuildingType.Stable, false)]
    [InlineData("Teutons", "Ram", TroopTrainingBuildingType.Workshop, true)]
    [InlineData("Teutons", "Settler", TroopTrainingBuildingType.Workshop, false)]
    public void IsTroopTypeAllowedForBuilding_ValidatesExpectedMatch(string tribe, string troopType, TroopTrainingBuildingType buildingType, bool expected)
    {
        Assert.Equal(expected, TroopCatalog.IsTroopTypeAllowedForBuilding(tribe, troopType, buildingType));
    }

    [Theory]
    [InlineData("Romans", "Legionnaire", 1)]
    [InlineData("Teutons", "Clubswinger", 11)]
    [InlineData("Teutons", "Scout", 14)]
    [InlineData("Gauls", "Ram", 27)]
    [InlineData("Egyptians", "Slave Militia", 51)]
    [InlineData("Huns", "Mercenary", 61)]
    [InlineData("Spartans", "Hoplite", 71)]
    public void ResolveTravianUnitId_ReturnsExpectedUnitId(string tribe, string troopType, int expected)
    {
        Assert.Equal(expected, TroopCatalog.ResolveTravianUnitId(tribe, troopType));
    }

    // The training form keys its amount inputs by the tribe-relative slot (t1..t10), NOT the global unit
    // id. This is the invariant the Official troop-training fix relies on: Gaul Ram is form input t7 even
    // though its unit id is u27. Keep slot and unit id distinct.
    [Theory]
    [InlineData("Romans", "Legionnaire", 1)]
    [InlineData("Gauls", "Phalanx", 1)]
    [InlineData("Gauls", "Ram", 7)]
    [InlineData("Gauls", "Trebuchet", 8)]
    [InlineData("Teutons", "Ram", 7)]
    [InlineData("Teutons", "Settler", 10)]
    public void ResolveTroopIndex_ReturnsTribeRelativeSlot(string tribe, string troopType, int expectedSlot)
    {
        Assert.Equal(expectedSlot, TroopCatalog.ResolveTroopIndex(troopType));
    }

    [Fact]
    public void ResolveTroopIndex_DiffersFromGlobalUnitId_ForNonRomanTribes()
    {
        // Gaul Ram: form slot t7, but unit id u27 — the bug the Official fix addresses (using the unit id
        // as the form index found input t27, which doesn't exist).
        Assert.Equal(7, TroopCatalog.ResolveTroopIndex("Ram"));
        Assert.Equal(27, TroopCatalog.ResolveTravianUnitId("Gauls", "Ram"));
    }
}
