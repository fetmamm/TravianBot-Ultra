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

    [Theory]
    [InlineData("Romans", "Legionnaire", TroopTrainingBuildingType.Barracks, true)]
    [InlineData("Romans", "Equites Legati", TroopTrainingBuildingType.Barracks, false)]
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
}
