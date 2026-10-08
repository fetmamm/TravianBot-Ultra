using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroInventoryProbePolicyTests
{
    [Fact]
    public void ShouldProbe_StaleEmptyInventoryOnlyAfterItsCooldown()
    {
        var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new HeroInventorySnapshot(
            new HeroInventoryResources(),
            now.AddMinutes(-20),
            HeroInventoryObservationSource.TransferDialog,
            ConsecutiveEmptyObservations: 1,
            NextProbeAtUtc: now.AddMinutes(1));

        Assert.False(HeroInventoryProbePolicy.ShouldProbe(snapshot, now));
        Assert.True(HeroInventoryProbePolicy.ShouldProbe(snapshot, now.AddMinutes(1)));
    }

    [Fact]
    public void ShouldProbe_DoesNotProbeKnownNonEmptyInventory()
    {
        var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new HeroInventorySnapshot(
            new HeroInventoryResources(Wood: 1),
            now.AddHours(-2),
            HeroInventoryObservationSource.HeroInventoryPage,
            NextProbeAtUtc: now.AddMinutes(-1));

        Assert.False(HeroInventoryProbePolicy.ShouldProbe(snapshot, now));
    }

    [Theory]
    [InlineData(1, 0d, 15)]
    [InlineData(1, 1d, 30)]
    [InlineData(2, 0d, 30)]
    [InlineData(2, 1d, 45)]
    [InlineData(3, 0d, 45)]
    [InlineData(9, 1d, 60)]
    public void EmptyProbeDelay_BacksOffAndCapsAtOneHour(
        int consecutiveEmptyObservations,
        double sample,
        int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            HeroInventoryProbePolicy.GetEmptyProbeDelay(consecutiveEmptyObservations, sample));
    }

    [Theory]
    [InlineData(1, 0d, 15)]
    [InlineData(2, 1d, 45)]
    [InlineData(3, 0d, 45)]
    [InlineData(8, 1d, 60)]
    public void ConstructionProbeDelay_UsesSharedProgressiveBackoff(
        int consecutiveInsufficientObservations,
        double sample,
        int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            HeroInventoryProbePolicy.GetConstructionProbeDelay(consecutiveInsufficientObservations, sample));
    }
}
