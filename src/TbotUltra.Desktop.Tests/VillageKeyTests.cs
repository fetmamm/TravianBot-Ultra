using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageKeyTests
{
    [Fact]
    public void FromComponents_WithCoordinates_UsesCoordinateKey()
    {
        Assert.Equal("xy:5|-7", VillageKey.FromComponents(5, -7, "123", "Capital"));
    }

    [Fact]
    public void FromComponents_CoordinatesTakePrecedenceOverNewdidAndName()
    {
        // Coordinates are the stable identity: a rename or a different newdid must not change the key.
        var byCoords = VillageKey.FromComponents(1, 2, "999", "Renamed");

        Assert.Equal(VillageKey.FromCoords(1, 2), byCoords);
    }

    [Fact]
    public void FromComponents_NoCoordinates_UsesTrimmedNewdid()
    {
        Assert.Equal("did:123", VillageKey.FromComponents(null, null, "  123  ", "Capital"));
    }

    [Fact]
    public void FromComponents_PartialCoordinates_FallsBackToNewdid()
    {
        // A single coordinate is not a usable coordinate identity.
        Assert.Equal("did:42", VillageKey.FromComponents(5, null, "42", "X"));
    }

    [Fact]
    public void FromComponents_NoCoordinatesOrNewdid_UsesLowercasedTrimmedName()
    {
        Assert.Equal("name:capital city", VillageKey.FromComponents(null, null, null, "  Capital City  "));
    }

    [Fact]
    public void FromComponents_NoIdentityAtAll_ReturnsEmptyNameKey()
    {
        Assert.Equal("name:", VillageKey.FromComponents(null, null, null, null));
    }

    [Fact]
    public void FromCoords_FormatsAsXy()
    {
        Assert.Equal("xy:-199|200", VillageKey.FromCoords(-199, 200));
    }
}
