using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionStatusSyncPolicyTests
{
    [Fact]
    public void PostLoginRound_OnlyCompleteFinalVillageCanSatisfyPendingSync()
    {
        var session = new AutomationSessionRuntime();
        var generation = session.ConstructionStatusSyncGeneration;

        Assert.False(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            CompleteStatus(), 12, 34, 1, 2));
        Assert.False(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            CompleteStatus() with { Buildings = [new Building(19, "Main Building", 1, null)] },
            12, 34, 2, 2));
        Assert.True(session.ConstructionStatusNeedsSync);

        Assert.True(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            CompleteStatus(), 12, 34, 2, 2));
        Assert.True(session.TryMarkConstructionStatusSynchronized(generation));
        Assert.False(session.ConstructionStatusNeedsSync);
    }

    [Fact]
    public void PostLoginRound_RejectsPartialCityAndDuplicateResourceFields()
    {
        var city = CompleteStatus(CityStatus.City);
        Assert.False(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            city with { Buildings = city.Buildings.Where(building => building.SlotId != 43).ToArray() },
            12, 34, 1, 1));

        var duplicateFields = city.ResourceFields.Take(17)
            .Append(city.ResourceFields[0]).ToArray();
        Assert.False(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            city with { ResourceFields = duplicateFields }, 12, 34, 1, 1));
        Assert.False(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            city with { CityStatus = CityStatus.Unknown }, 12, 34, 1, 1));
        Assert.False(ConstructionStatusSyncPolicy.IsCompleteFinalVisit(
            city, 99, 34, 1, 1));
    }

    [Fact]
    public void NewRequestDuringRound_IsNotClearedByOlderObservation()
    {
        var session = new AutomationSessionRuntime();
        var generation = session.ConstructionStatusSyncGeneration;
        session.RequestConstructionStatusSync();

        Assert.False(session.TryMarkConstructionStatusSynchronized(generation));
        Assert.True(session.ConstructionStatusNeedsSync);
    }

    private static VillageStatus CompleteStatus(CityStatus cityStatus = CityStatus.Village) => new(
        "Village",
        [],
        new Dictionary<string, string>(),
        Enumerable.Range(1, 18)
            .Select(slot => new ResourceField(slot, "Woodcutter", "Woodcutter", 1, null)).ToArray(),
        Enumerable.Range(19, 22)
            .Concat(cityStatus == CityStatus.City ? [41, 42, 43] : [])
            .Select(slot => new Building(slot, "Empty", 0, null)).ToArray(),
        [],
        ActiveConstructionsFromOverview: true,
        ActiveVillageCoordX: 12,
        ActiveVillageCoordY: 34,
        CityStatus: cityStatus);
}
