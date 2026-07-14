using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class VillageIdentityReconcilerTests
{
    [Fact]
    public void FindByNameOrCoordinates_PrefersExactNormalizedName()
    {
        Village[] villages =
        [
            new("pha", "/dorf1.php?newdid=1", false, 10, 20),
            new("PHA", "/dorf1.php?newdid=2", false, 30, 40),
        ];

        var selected = VillageIdentityReconciler.FindByNameOrCoordinates(villages, "pha (10|20)", (30, 40));

        Assert.Equal("/dorf1.php?newdid=1", selected?.Url);
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
}
