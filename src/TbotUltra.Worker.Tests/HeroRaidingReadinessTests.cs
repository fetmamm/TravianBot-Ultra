using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingReadinessTests
{
    [Fact]
    public void HeroHomeVillage_DidOnlyLinkKeepsNameForSidebarResolution()
    {
        const string attributesState = """
            {"name":"GAPP","away":false,"x":null,"y":null}
            """;
        Village[] villages =
        [
            new("HEJ", "dorf1.php?newdid=12915", CoordX: -115, CoordY: 94),
            new("GAPP", "dorf1.php?newdid=18734", CoordX: -122, CoordY: 83),
        ];

        var home = TravianClient.ParseHeroHomeVillageInfo(attributesState);

        Assert.Equal("GAPP", home.Name);
        Assert.False(home.Away);
        Assert.Equal((null, null), (home.X, home.Y));
        Assert.Equal((-122, 83), TravianClient.ResolveHeroRaidingVillage(home.Name!, villages));
    }

    [Fact]
    public void HeroHomeVillage_ExplicitCoordinatesRemainAvailable()
    {
        var home = TravianClient.ParseHeroHomeVillageInfo(
            """{"name":"GAPP","away":false,"x":-122,"y":83}""");

        Assert.Equal(("GAPP", false, -122, 83), home);
    }

    [Fact]
    public void ResolveHeroRaidingVillage_UsesUniqueLiveSidebarMatch()
    {
        Village[] villages =
        [
            new("SATA", "dorf1.php?newdid=38455", CoordX: 156, CoordY: -114),
            new("Other", "dorf1.php?newdid=123", CoordX: 1, CoordY: 2),
        ];

        Assert.Equal((156, -114), TravianClient.ResolveHeroRaidingVillage(" SATA ", villages));
    }

    [Fact]
    public void ResolveHeroRaidingVillage_DoesNotGuessWhenNameIsDuplicated()
    {
        Village[] villages =
        [
            new("SATA", "dorf1.php?newdid=38455", CoordX: 156, CoordY: -114),
            new("SATA", "dorf1.php?newdid=123", CoordX: 1, CoordY: 2),
        ];

        Assert.Equal((null, null), TravianClient.ResolveHeroRaidingVillage("SATA", villages));
    }

    [Fact]
    public void MapVillage_ResolvesDuplicateNameUsingClickedTileDetails()
    {
        Village[] villages =
        [
            new("GAPP", "dorf1.php?newdid=18734", CoordX: -122, CoordY: 83),
            new("GAPP", "dorf1.php?newdid=91234", CoordX: 22, CoordY: -9),
        ];

        Assert.Equal((null, null), TravianClient.ResolveHeroRaidingVillage("GAPP", villages));
        Assert.Equal((-122, 83), TravianClient.ParseHeroRaidingMapVillage(
            """{"name":"GAPP","x":-122,"y":83}""", "GAPP"));
    }

    [Theory]
    [InlineData("{\"name\":\"Other\",\"x\":-122,\"y\":83}")]
    [InlineData("{\"name\":\"GAPP\",\"x\":null,\"y\":83}")]
    [InlineData("{\"name\":\"GAPP\",\"x\":-122,\"y\":null}")]
    [InlineData(null)]
    public void MapVillage_RejectsUnverifiedDetails(string? details)
    {
        Assert.Equal((null, null), TravianClient.ParseHeroRaidingMapVillage(details, "GAPP"));
    }

    [Fact]
    public void ResolveHeroRaidingVillage_DoesNotGuessWhenCoordinatesAreMissing()
    {
        Village[] villages = [new("SATA", "dorf1.php?newdid=38455")];

        Assert.Equal((null, null), TravianClient.ResolveHeroRaidingVillage("SATA", villages));
    }
}
