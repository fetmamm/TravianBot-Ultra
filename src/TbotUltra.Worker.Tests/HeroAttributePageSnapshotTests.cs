using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroAttributePageSnapshotTests
{
    [Fact]
    public void AttributeRead_UsesKnownSnapshotUnlessNewPointsAreSignalled()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Hero",
            "TravianClient.Hero.Status.cs"));
        var methodStart = source.IndexOf(
            "public async Task<HeroAttributeSnapshot> ReadHeroAttributeSnapshotAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task<(string? Name, bool Away, int? X, int? Y)> ReadHeroHomeVillageInfoAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);

        var method = source[methodStart..methodEnd];
        var cacheRead = method.IndexOf("TryGetCachedHeroAttributeSnapshot", StringComparison.Ordinal);
        var knownWithoutNewPoints = method.IndexOf(
            "cachedSnapshot is not null && !quick.HasUnassignedPointsSignal",
            StringComparison.Ordinal);
        var navigation = method.IndexOf("EnsurePageForReadAsync(Paths.HeroAttributes", StringComparison.Ordinal);

        Assert.True(cacheRead >= 0 && knownWithoutNewPoints > cacheRead && navigation > knownWithoutNewPoints,
            "Known Hero attributes must avoid navigation unless the sidebar signals new points.");
    }

    [Fact]
    public void LiveAttributeRead_AlsoProjectsMovementAndReturnTimerFromTheSamePage()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Hero",
            "TravianClient.Hero.Status.cs"));
        var methodStart = source.IndexOf(
            "public async Task<HeroAttributeSnapshot> ReadHeroAttributeSnapshotAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task<(string? Name, bool Away, int? X, int? Y)> ReadHeroHomeVillageInfoAsync",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var attributesNavigation = method.IndexOf("EnsurePageForReadAsync(Paths.HeroAttributes", StringComparison.Ordinal);
        var runtimeRead = method.IndexOf("ReadHeroStatusAsync(cancellationToken)", StringComparison.Ordinal);
        Assert.True(runtimeRead > attributesNavigation);
        Assert.Contains("HomeVillageHeroAway = heroAway", method, StringComparison.Ordinal);
        Assert.Contains("SecondsUntilReturn = returnSeconds", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ToSnapshot_PreservesVerifiedLiveValues()
    {
        var page = new HeroAttributePageSnapshot(
            Ok: true,
            AttributeCount: 4,
            FreePoints: 4,
            FightingStrength: 12,
            OffenceBonus: 3,
            DefenceBonus: 5,
            Resources: 28);

        var snapshot = page.ToSnapshot();

        Assert.Equal(4, snapshot.FreePoints);
        Assert.Equal(28, snapshot.Resources);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    public void ToSnapshot_RejectsFailedOrIncompletePageReads(bool ok, int attributeCount)
    {
        var page = new HeroAttributePageSnapshot(Ok: ok, AttributeCount: attributeCount);

        Assert.Throws<InvalidOperationException>(() => page.ToSnapshot());
    }
}
