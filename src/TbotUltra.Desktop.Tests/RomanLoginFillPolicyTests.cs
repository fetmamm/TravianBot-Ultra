using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class RomanLoginFillPolicyTests
{
    [Fact]
    public void Resolve_ThreeLiveConstructions_CompletesLoginFill()
    {
        var status = CreateStatus(
            new ActiveConstruction(ConstructionKind.Building, "Building 1", null, null, null),
            new ActiveConstruction(ConstructionKind.Building, "Building 2", null, null, null),
            new ActiveConstruction(ConstructionKind.Resource, "Resource", null, null, null));

        var result = RomanLoginFillPolicy.Resolve(status, travianPlusActive: true, hasPendingResource: true, hasPendingBuilding: true);

        Assert.Equal(RomanLoginFillState.Complete, result.State);
        Assert.Equal(2, result.BuildingCount);
        Assert.Equal(1, result.ResourceCount);
    }

    [Fact]
    public void Resolve_TwoBuildingsWithOnlyResourcePending_RequiresContinuation()
    {
        var status = CreateStatus(
            new ActiveConstruction(ConstructionKind.Building, "Building 1", null, null, null),
            new ActiveConstruction(ConstructionKind.Building, "Building 2", null, null, null));

        var result = RomanLoginFillPolicy.Resolve(status, travianPlusActive: true, hasPendingResource: true, hasPendingBuilding: false);

        Assert.Equal(RomanLoginFillState.NeedsComplementaryCategory, result.State);
    }

    [Fact]
    public void Resolve_TwoBuildingsWithoutResourcePending_IsExplicitlyBlocked()
    {
        var status = CreateStatus(
            new ActiveConstruction(ConstructionKind.Building, "Building 1", null, null, null),
            new ActiveConstruction(ConstructionKind.Building, "Building 2", null, null, null));

        var result = RomanLoginFillPolicy.Resolve(status, travianPlusActive: true, hasPendingResource: false, hasPendingBuilding: true);

        Assert.Equal(RomanLoginFillState.Blocked, result.State);
        Assert.Contains("resource", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static VillageStatus CreateStatus(params ActiveConstruction[] active) => new(
        ActiveVillage: "Roman village",
        Villages: [],
        Resources: new Dictionary<string, string>(),
        ResourceFields: [],
        Buildings: [],
        BuildQueue: [],
        Tribe: "Romans",
        ActiveConstructions: active.ToList(),
        ActiveConstructionsFromOverview: true);
}
