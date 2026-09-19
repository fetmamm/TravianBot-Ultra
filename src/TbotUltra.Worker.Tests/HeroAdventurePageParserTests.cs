using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroAdventurePageParserTests
{
    [Fact]
    public void MissingRallyPointFixture_ResolvesExactHeroHomeVillage()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hero_adv_noRp.txt");
        var html = File.ReadAllText(fixturePath);

        var result = HeroAdventurePageParser.ParseMissingRallyPoint(html);

        Assert.NotNull(result);
        Assert.Equal(24443, result.VillageId);
        Assert.Equal("WHY", result.VillageName);
        Assert.Equal(164, result.CoordX);
        Assert.Equal(110, result.CoordY);
    }

    [Fact]
    public void AdventurePageWithoutMissingSignal_DoesNotRequestRepair()
    {
        const string html = "<div id=\"heroAdventure\"><button>Explore</button></div>";

        Assert.Null(HeroAdventurePageParser.ParseMissingRallyPoint(html));
    }
}
