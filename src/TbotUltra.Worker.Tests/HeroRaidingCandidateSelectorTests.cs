using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingCandidateSelectorTests
{
    [Fact]
    public void Select_ExcludesOccupiedEmptyStaleFilteredAndExplicitlyExcludedOases()
    {
        var now = DateTimeOffset.UtcNow;
        MapOasisEntry Oasis(int x, int y, string type, string animals, bool occupied = false, int ageMinutes = 0) =>
            new(x, y, occupied, type, type, animals, "", "", now.AddMinutes(-ageMinutes));
        var oases = new[]
        {
            Oasis(1, 1, "Wood", "Rat 2"),
            Oasis(2, 2, "Wood", "Rat 2", occupied: true),
            Oasis(3, 3, "Wood", ""),
            Oasis(4, 4, "Crop", "Rat 2"),
            Oasis(5, 5, "Clay", "Rat 2", ageMinutes: 11),
            Oasis(6, 6, "Iron", "Rat 2"),
            Oasis(30, 30, "Wood", "Rat 2"),
            Oasis(8, 8, "Wood", "Dragon 2"),
        };

        var result = HeroRaidingCandidateSelector.Select(
            oases, 0, 0, 20, 10, ["Wood", "Clay", "Iron"],
            new HashSet<(int X, int Y)> { (6, 6) }, now);

        var candidate = Assert.Single(result);
        Assert.Equal((1, 1), (candidate.Oasis.X, candidate.Oasis.Y));
        Assert.Equal(80, candidate.EstimatedBountyPerResource);
    }

    [Fact]
    public void Select_PrefersBountyPerDistanceAndAllowsVerifiedWrap()
    {
        var now = DateTimeOffset.UtcNow;
        var oases = new[]
        {
            new MapOasisEntry(4, 0, false, "Wood 25%", "Wood", "Rat 1", "", "", now),
            new MapOasisEntry(8, 0, false, "Wood 25%", "Wood", "Bear 3", "", "", now),
            new MapOasisEntry(-200, 0, false, "Wood 25%", "Wood", "Rat 1", "", "", now),
        };

        var ordinary = HeroRaidingCandidateSelector.Select(
            oases, 200, 0, 20, 10, ["Wood"], new HashSet<(int X, int Y)>(), now);
        Assert.Empty(ordinary);

        var wrapped = HeroRaidingCandidateSelector.Select(
            oases, 200, 0, 20, 10, ["Wood"], new HashSet<(int X, int Y)>(), now, wrappedWorldWidth: 401);
        Assert.Equal((-200, 0), (Assert.Single(wrapped).Oasis.X, wrapped[0].Oasis.Y));
    }
}
