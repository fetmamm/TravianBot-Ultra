using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionSlotCapacityTests
{
    [Theory]
    [InlineData("Romans", 3)]
    [InlineData("Gauls", 2)]
    [InlineData("Teutons", 2)]
    [InlineData("Huns", 2)]
    [InlineData("Egyptians", 2)]
    [InlineData("Spartans", 2)]
    [InlineData("Unknown", 2)]
    [InlineData(null, 2)]
    public void Resolve_UsesOnlyTheGivenVillageTribe(string? tribe, int expected)
    {
        Assert.Equal(expected, ConstructionSlotCapacity.Resolve(tribe));
    }

    [Fact]
    public void BuildVillageSignature_ChangesWhenOnlyTribeChanges()
    {
        var egyptian = new Village("FET", "dorf1.php?newdid=31460", true, 112, 6, 101, null, "Egyptians");
        var roman = egyptian with { Tribe = "Romans" };

        Assert.NotEqual(
            MainWindow.BuildVillageSignature([egyptian]),
            MainWindow.BuildVillageSignature([roman]));
    }
}
