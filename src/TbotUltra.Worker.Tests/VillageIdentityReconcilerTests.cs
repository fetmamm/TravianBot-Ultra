using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class VillageIdentityReconcilerTests
{
    [Fact]
    public void FindByNameOrCoordinates_PrefersCoordinatesWhenNamesAreDuplicated()
    {
        Village[] villages =
        [
            new("pha", "/dorf1.php?newdid=1", false, 10, 20),
            new("PHA", "/dorf1.php?newdid=2", false, 30, 40),
        ];

        var selected = VillageIdentityReconciler.FindByNameOrCoordinates(villages, "pha (10|20)", (30, 40));

        Assert.Equal("/dorf1.php?newdid=2", selected?.Url);
    }

    [Fact]
    public void FindByNameOrCoordinates_UsesCoordinatesWhenNameChanged()
    {
        Village[] villages = [new("Old name", "/dorf1.php?newdid=1", false, 10, 20)];

        var selected = VillageIdentityReconciler.FindByNameOrCoordinates(villages, "New name", (10, 20));

        Assert.Equal("Old name", selected?.Name);
    }

    [Fact]
    public void FindByNameOrCoordinates_ReturnsNullWithoutStableIdentity()
    {
        Village[] villages = [new("Other", "/dorf1.php?newdid=1", false, 10, 20)];

        Assert.Null(VillageIdentityReconciler.FindByNameOrCoordinates(villages, "Missing", (null, null)));
    }

    [Fact]
    public void MergeFreshWithCached_KeepsFreshSidebarCoordinatesForDuplicateNames()
    {
        Village[] cached =
        [
            new("New village", "/dorf1.php?newdid=1", false, 93, -19, 90),
            new("New village", "/dorf1.php?newdid=2", false, 93, -19, 95),
        ];
        var fresh = new Village("New village", "/dorf1.php?newdid=2", false, 93, -17, 96);

        var merged = VillageIdentityReconciler.MergeFreshWithCached(fresh, cached);

        Assert.Equal(93, merged.CoordX);
        Assert.Equal(-17, merged.CoordY);
        Assert.Equal(96, merged.Population);
    }

    [Fact]
    public void ReconcileRenamedByCoordinates_AllowsRenamingToAnExistingVillageName()
    {
        Village[] cached =
        [
            new("Existing", "/dorf1.php?newdid=1", false, 10, 20),
            new("Old name", "/dorf1.php?newdid=2", false, 30, 40),
        ];

        var updated = VillageIdentityReconciler.ReconcileRenamedByCoordinates(cached, "Existing", (30, 40));

        Assert.NotNull(updated);
        Assert.Equal("Existing", updated![0].Name);
        Assert.Equal("Existing", updated[1].Name);
        Assert.Equal(10, updated[0].CoordX);
        Assert.Equal(30, updated[1].CoordX);
    }

    [Fact]
    public void EnrichActiveVillageCoordinates_UpdatesOnlyMatchingDidForDuplicateNames()
    {
        Village[] villages =
        [
            new("New village", "/dorf1.php?newdid=1"),
            new("New village", "/dorf1.php?newdid=2"),
        ];

        var updated = VillageIdentityReconciler.EnrichActiveVillageCoordinates(villages, 2, (30, 40));

        Assert.Null(updated[0].CoordX);
        Assert.Null(updated[0].CoordY);
        Assert.Equal(30, updated[1].CoordX);
        Assert.Equal(40, updated[1].CoordY);
    }

    [Fact]
    public void EnrichActiveVillageCoordinates_NeverMatchesDuplicateVillagesByName()
    {
        Village[] villages =
        [
            new("New village", "/dorf1.php?newdid=1", CoordX: 10, CoordY: 20),
            new("New village", "/dorf1.php?newdid=2"),
        ];

        var updated = VillageIdentityReconciler.EnrichActiveVillageCoordinates(villages, null, (10, 20));

        Assert.Equal(10, updated[0].CoordX);
        Assert.Null(updated[1].CoordX);
    }

    [Fact]
    public void BuildStableVillageToken_DistinguishesSameNameVillagesByDidOrCoordinates()
    {
        var first = VillageIdentityReconciler.BuildStableVillageToken(1, (10, 20), "New village");
        var second = VillageIdentityReconciler.BuildStableVillageToken(2, (10, 20), "New village");
        var coordinateFallback = VillageIdentityReconciler.BuildStableVillageToken(null, (30, 40), "New village");

        Assert.Equal("1", first);
        Assert.Equal("2", second);
        Assert.Equal("xy:30|40", coordinateFallback);
    }
}
