using TbotUltra.Worker.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BuildingOverviewDomParserTests
{
    [Fact]
    public void Parse_EmptyBuildingLink_IsNotOccupancyEvidence()
    {
        var scan = BuildingOverviewDomParser.Parse(
        [
            new BuildingOverviewSlotSnapshot
            {
                ClassName = "buildingSlot a31 aid31 egyptian",
                OuterHtml = "<div class='buildingSlot a31 aid31 egyptian' data-aid='31'><a href='/build.php?id=31' class='emptyBuildingSlot'></a></div>",
                OccupiedEvidence = true,
            },
        ]);

        var slot = Assert.Single(scan.Buildings).Value;
        Assert.Equal(31, slot.SlotId);
        Assert.Equal("Empty", slot.BuildingName);
        Assert.False(slot.HasOccupancyEvidence);
        Assert.Equal(0, slot.Level);
    }

    [Fact]
    public void Parse_CompleteOverviewWithExtraSlots_ConfirmsCity()
    {
        var slots = Enumerable.Range(19, 25)
            .Select(slot => new BuildingOverviewSlotSnapshot
            {
                ClassName = $"buildingSlot aid{slot} g0",
                OuterHtml = $"<div class='buildingSlot aid{slot} g0' data-aid='{slot}'></div>",
            })
            .ToList();
        slots[0] = new BuildingOverviewSlotSnapshot
        {
            ClassName = "buildingSlot aid19 g15",
            OuterHtml = "<div class='buildingSlot aid19 g15'>Main Building Level 20</div>",
            NameText = "Main Building",
            LevelText = "20",
            OccupiedEvidence = true,
        };
        slots[20] = new BuildingOverviewSlotSnapshot
        {
            ClassName = "buildingSlot aid39 g16",
            OuterHtml = "<div class='buildingSlot aid39 g16'>Rally Point Level 1</div>",
            NameText = "Rally Point",
            LevelText = "1",
            OccupiedEvidence = true,
        };

        var scan = BuildingOverviewDomParser.Parse(slots);

        Assert.Equal(CityStatus.City, scan.CityStatus);
        Assert.Contains(43, scan.Buildings.Keys);
    }
}
