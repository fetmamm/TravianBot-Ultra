using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageListUpdatePolicyTests
{
    [Fact]
    public void HasPotentialMembershipMismatch_DetectsLostVillagesBeforeMutation()
    {
        var known = Enumerable.Range(1, 8)
            .Select(index => new TestVillage($"village-{index}", index))
            .ToList();
        var liveSidebar = known.Take(5).ToList();

        var mismatch = VillageListUpdatePolicy.HasPotentialMembershipMismatch(
            liveSidebar,
            known,
            village => village.Key);

        Assert.True(mismatch);
    }

    [Fact]
    public void HasPotentialMembershipMismatch_AcceptsSameVillageSet()
    {
        var known = Enumerable.Range(1, 5)
            .Select(index => new TestVillage($"village-{index}", index))
            .ToList();

        var mismatch = VillageListUpdatePolicy.HasPotentialMembershipMismatch(
            known.AsEnumerable().Reverse().ToList(),
            known,
            village => village.Key);

        Assert.False(mismatch);
    }

    [Fact]
    public void PreserveKnownVillages_MergesTransientPartialRefresh()
    {
        var existing = Enumerable.Range(1, 8)
            .Select(index => new TestVillage($"village-{index}", index))
            .ToList();
        var partial = new[] { new TestVillage("village-2", 999) };

        var result = VillageListUpdatePolicy.PreserveKnownVillages(
            partial,
            existing,
            village => village.Key);

        Assert.Equal(8, result.Count);
        Assert.Equal(999, Assert.Single(result, village => village.Key == "village-2").Value);
    }

    [Fact]
    public void PreserveKnownVillages_AcceptsCompleteRefreshWithNewVillage()
    {
        var existing = new[]
        {
            new TestVillage("village-1", 1),
            new TestVillage("village-2", 2),
        };
        var complete = new[]
        {
            new TestVillage("village-1", 10),
            new TestVillage("village-2", 20),
            new TestVillage("village-3", 30),
        };

        var result = VillageListUpdatePolicy.PreserveKnownVillages(
            complete,
            existing,
            village => village.Key);

        Assert.Equal(complete, result);
    }

    private sealed record TestVillage(string Key, int Value);
}
