using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class UtilityVillagePayloadTests
{
    private static readonly VillageSelectionItem[] DuplicateNameVillages =
    [
        new()
        {
            Name = "New village",
            Url = "https://example.test/dorf1.php?newdid=28803",
            CoordX = 93,
            CoordY = -17,
        },
        new()
        {
            Name = "New village",
            Url = "https://example.test/dorf1.php?newdid=28805",
            CoordX = 93,
            CoordY = -19,
        },
    ];

    [Fact]
    public void BuildUtilityVillagePayload_DuplicateNames_UsesActiveCoordinates()
    {
        var payload = MainWindow.BuildUtilityVillagePayload(
            "New village",
            93,
            -19,
            "xy:93|-17",
            DuplicateNameVillages);

        Assert.NotNull(payload);
        Assert.Equal("xy:93|-19", payload[BotOptionPayloadKeys.TargetVillageKey]);
        Assert.EndsWith("newdid=28805", payload[BotOptionPayloadKeys.TargetVillageUrl]);
    }

    [Fact]
    public void BuildUtilityVillagePayload_NoCoordinates_UsesStableWorkingKey()
    {
        var payload = MainWindow.BuildUtilityVillagePayload(
            "New village",
            null,
            null,
            "xy:93|-19",
            DuplicateNameVillages);

        Assert.NotNull(payload);
        Assert.Equal("xy:93|-19", payload[BotOptionPayloadKeys.TargetVillageKey]);
        Assert.EndsWith("newdid=28805", payload[BotOptionPayloadKeys.TargetVillageUrl]);
    }

    [Fact]
    public void BuildUtilityVillagePayload_DuplicateNameWithoutStableIdentity_DoesNotGuess()
    {
        var payload = MainWindow.BuildUtilityVillagePayload(
            "New village",
            null,
            null,
            null,
            DuplicateNameVillages);

        Assert.Null(payload);
    }
}
