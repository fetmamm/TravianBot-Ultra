using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

internal static class VillageStatusCompleteness
{
    internal static bool HasCompleteResourceFieldSnapshot(IReadOnlyList<ResourceField>? fields)
    {
        if (fields is null)
        {
            return false;
        }

        var bySlot = fields
            .Where(field => field.SlotId is >= 1 and <= 18)
            .GroupBy(field => field.SlotId!.Value)
            .ToList();
        if (bySlot.Count != 18)
        {
            return false;
        }

        return bySlot.All(group =>
        {
            var field = group.First();
            return field.Level is >= 0
                && (BuildingCatalogService.GidForName(field.Name) is not null
                    || BuildingCatalogService.GidForName(field.FieldType) is not null);
        });
    }
}
