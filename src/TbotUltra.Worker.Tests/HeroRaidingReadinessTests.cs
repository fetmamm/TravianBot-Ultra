using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingReadinessTests
{
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
    public void ResolveHeroRaidingVillage_DoesNotGuessWhenCoordinatesAreMissing()
    {
        Village[] villages = [new("SATA", "dorf1.php?newdid=38455")];

        Assert.Equal((null, null), TravianClient.ResolveHeroRaidingVillage("SATA", villages));
    }
}
