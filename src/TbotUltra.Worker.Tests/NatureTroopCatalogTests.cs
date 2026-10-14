using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class NatureTroopCatalogTests
{
    [Fact]
    public void Catalog_ContainsAllOfficialNatureUnits()
    {
        Assert.Equal(Enumerable.Range(31, 10), NatureTroopCatalog.All.Select(troop => troop.UnitId));
        Assert.Equal(25, NatureTroopCatalog.All[0].DefenceAgainstInfantry);
        Assert.Equal(520, NatureTroopCatalog.All[^1].DefenceAgainstCavalry);
        Assert.Equal(200, NatureTroopCatalog.All[^1].BountyPerResource);
    }

    [Fact]
    public void ParseGarrison_CalculatesFourResourceBounty()
    {
        Assert.True(NatureTroopCatalog.TryParseGarrison("Wild Boar 5, Bear 4", out var garrison));
        Assert.True(NatureTroopCatalog.TryCalculateBountyPerResource(garrison, out var bounty));
        Assert.Equal(880, bounty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Dragon 3")]
    [InlineData("Bear ?")]
    [InlineData("Bear 0")]
    [InlineData("Rat 2, Rat 3")]
    public void ParseGarrison_RejectsUnknownOrMalformedData(string animals)
    {
        Assert.False(NatureTroopCatalog.TryParseGarrison(animals, out _));
    }
}
