namespace TbotUltra.Worker.Domain;

public static class BuildingSlotPolicy
{
    public const int FirstOrdinarySlot = 19;
    public const int LastVillageOrdinarySlot = 38;
    public const int FirstCityExtraSlot = 41;
    public const int LastCityExtraSlot = 43;

    public static IReadOnlyList<int> OrdinarySlots(CityStatus cityStatus)
    {
        var slots = Enumerable.Range(FirstOrdinarySlot, LastVillageOrdinarySlot - FirstOrdinarySlot + 1).ToList();
        if (cityStatus == CityStatus.City)
        {
            slots.AddRange(Enumerable.Range(FirstCityExtraSlot, LastCityExtraSlot - FirstCityExtraSlot + 1));
        }

        return slots;
    }

    public static IReadOnlyList<int> OverviewSlots(CityStatus cityStatus) =>
        [.. OrdinarySlots(cityStatus), 39, 40];

    public static bool IsOrdinarySlot(int slotId, CityStatus cityStatus) =>
        slotId is >= FirstOrdinarySlot and <= LastVillageOrdinarySlot
        || (cityStatus == CityStatus.City && slotId is >= FirstCityExtraSlot and <= LastCityExtraSlot);

    public static bool IsPotentialOrdinarySlot(int slotId) =>
        slotId is >= FirstOrdinarySlot and <= LastVillageOrdinarySlot
        || slotId is >= FirstCityExtraSlot and <= LastCityExtraSlot;
}
