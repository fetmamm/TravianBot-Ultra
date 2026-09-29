using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageListUpdatePolicyTests
{
    [Fact]
    public void DifferentAvatarGuard_BlocksReconciliationAndExplainsAutomaticRecovery()
    {
        var projectRoot = TbotUltra.Worker.ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.ContinuousLoop.cs"));
        var guardIndex = source.IndexOf(
            "VillageListUpdatePolicy.IsDifferentAvatar",
            StringComparison.Ordinal);
        var reconcileIndex = source.IndexOf(
            "ReconcileConfirmedVillageList(snapshot.Villages",
            StringComparison.Ordinal);

        Assert.True(guardIndex >= 0);
        Assert.True(reconcileIndex > guardIndex);
        Assert.Contains("village Auto settings and queue items were not changed", source, StringComparison.Ordinal);
        Assert.Contains("Continuous Loop will verify it and resume automatically", source, StringComparison.Ordinal);
    }

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
    public void IsDifferentAvatar_DetectsVerifiedProfileWithoutAnyKnownVillage()
    {
        var known = new[]
        {
            new TestVillage("xy:101|114", 1),
            new TestVillage("xy:164|110", 2),
        };
        var sitterProfile = new[]
        {
            new TestVillage("xy:62|-50", 10),
            new TestVillage("xy:33|-75", 20),
        };

        var differentAvatar = VillageListUpdatePolicy.IsDifferentAvatar(
            sitterProfile,
            known,
            village => village.Key);

        Assert.True(differentAvatar);
    }

    [Fact]
    public void IsDifferentAvatar_AllowsPartialConfirmedListFromSameAvatar()
    {
        var known = new[]
        {
            new TestVillage("xy:101|114", 1),
            new TestVillage("xy:164|110", 2),
        };
        var sameAvatar = new[]
        {
            new TestVillage("xy:164|110", 20),
            new TestVillage("xy:69|85", 30),
        };

        var differentAvatar = VillageListUpdatePolicy.IsDifferentAvatar(
            sameAvatar,
            known,
            village => village.Key);

        Assert.False(differentAvatar);
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
