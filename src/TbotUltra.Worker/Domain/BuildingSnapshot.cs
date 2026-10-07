namespace TbotUltra.Worker.Domain;

public sealed record BuildingSnapshot(
    string? Account,
    string? ActiveVillage,
    string? Tribe,
    bool? IsCapital,
    long? WarehouseCapacity,
    long? GranaryCapacity,
    IReadOnlyList<Building>? Buildings,
    IReadOnlyList<ResourceField>? ResourceFields,
    IReadOnlyList<ActiveConstruction>? ActiveConstructions = null,
    bool ActiveConstructionsFromOverview = false,
    CityCapability CityCapability = CityCapability.Unknown,
    CityStatus CityStatus = CityStatus.Unknown,
    WatchtowerStatus? WatchtowerStatus = null)
{
    public static BuildingSnapshot FromVillageStatus(string? account, VillageStatus status)
        => new(
            account,
            status.ActiveVillage,
            status.Tribe,
            status.IsCapital,
            status.WarehouseCapacity,
            status.GranaryCapacity,
            status.Buildings,
            status.ResourceFields,
            status.ActiveConstructions,
            status.ActiveConstructionsFromOverview,
            status.CityCapability,
            status.CityStatus,
            status.WatchtowerStatus);
}
