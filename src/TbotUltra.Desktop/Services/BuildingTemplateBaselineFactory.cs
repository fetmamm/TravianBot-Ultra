using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class BuildingTemplateBaselineFactory
{
    public static VillageStatus Create(string tribe)
    {
        var resourceNames = new[]
        {
            "Woodcutter", "Woodcutter", "Woodcutter", "Woodcutter",
            "Clay Pit", "Clay Pit", "Clay Pit", "Clay Pit",
            "Iron Mine", "Iron Mine", "Iron Mine", "Iron Mine",
            "Cropland", "Cropland", "Cropland", "Cropland", "Cropland", "Cropland",
        };
        var fields = resourceNames
            .Select((name, index) => new ResourceField(index + 1, name, name, 0, null))
            .ToList();

        return new VillageStatus(
            "Template baseline",
            [],
            new Dictionary<string, string>(),
            fields,
            [new Building(26, "Main Building", 1, null, 15)],
            [],
            string.IsNullOrWhiteSpace(tribe) ? "Unknown" : tribe,
            WarehouseCapacity: 800,
            GranaryCapacity: 800);
    }
}
