using TbotUltra.Core.Construction;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionAffordabilityPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_InsufficientVillageAndHeroResources_BlocksWithoutBuildPageNavigation()
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (100, 100, 100, 100),
            production: (100, 100, 100, 100),
            hero: new ConstructionResourceAmounts(50, 50, 50, 50),
            heroNextProbeAt: Now.AddMinutes(20)));

        Assert.Equal(ConstructionAffordabilityOutcome.Blocked, result.Outcome);
        Assert.Equal(Now.AddMinutes(15), result.NextAttemptAtUtc);
        Assert.False(result.ShouldOpenBuildPage);
        Assert.False(result.ShouldRevalidateHero);
    }

    [Fact]
    public void Evaluate_DueHeroRevalidation_RequestsOneDirectRefreshBeforeBuildPage()
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (100, 100, 100, 100),
            production: (100, 100, 100, 100),
            hero: new ConstructionResourceAmounts(50, 50, 50, 50),
            heroNextProbeAt: Now));

        Assert.Equal(ConstructionAffordabilityOutcome.Unknown, result.Outcome);
        Assert.True(result.ShouldRevalidateHero);
        Assert.False(result.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_HeroCanCoverEveryDeficit_AllowsRecoveryNavigation()
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (900, 900, 900, 900),
            production: (100, 100, 100, 100),
            hero: new ConstructionResourceAmounts(100, 100, 100, 100)));

        Assert.Equal(ConstructionAffordabilityOutcome.Recoverable, result.Outcome);
        Assert.Equal(ConstructionRecoveryKind.Hero, result.Recovery);
        Assert.True(result.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_HeroCanCoverDeficit_WithoutProductionRead()
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (900, 900, 900, 900),
            production: (null, null, null, null),
            hero: new ConstructionResourceAmounts(100, 100, 100, 100)));

        Assert.Equal(ConstructionAffordabilityOutcome.Recoverable, result.Outcome);
        Assert.Equal(ConstructionRecoveryKind.Hero, result.Recovery);
        Assert.True(result.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_HeroPerResourceLimitCannotCoverDeficit_BlocksNavigation()
    {
        var request = Request(
            stock: (100, 1000, 1000, 1000),
            production: (100, 100, 100, 100),
            hero: new ConstructionResourceAmounts(2000, 0, 0, 0),
            heroNextProbeAt: Now.AddMinutes(30));

        var result = ConstructionAffordabilityPlanner.Evaluate(request with
        {
            Hero = request.Hero with { MaxUsePerResource = 500 },
        });

        Assert.Equal(ConstructionAffordabilityOutcome.Blocked, result.Outcome);
        Assert.False(result.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_DueHeroRevalidationCannotBypassKnownPerResourceLimit()
    {
        var request = Request(
            stock: (100, 1000, 1000, 1000),
            production: (600, 100, 100, 100),
            hero: new ConstructionResourceAmounts(2000, 0, 0, 0),
            heroNextProbeAt: Now);

        var result = ConstructionAffordabilityPlanner.Evaluate(request with
        {
            Hero = request.Hero with { MaxUsePerResource = 500 },
        });

        Assert.Equal(ConstructionAffordabilityOutcome.Blocked, result.Outcome);
        Assert.False(result.ShouldOpenBuildPage);
        Assert.False(result.ShouldRevalidateHero);
        Assert.Equal(Now.AddMinutes(40), result.NextAttemptAtUtc);
    }

    [Fact]
    public void Evaluate_NpcCanRedistributeTotalStock_WhenAllLocalGoldGatesPass()
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (2000, 2000, 0, 0),
            production: (100, 100, 100, 100),
            heroEnabled: false,
            npcEnabled: true,
            gold: 120,
            goldLimit: 100,
            dailyBudgetAvailable: true));

        Assert.Equal(ConstructionAffordabilityOutcome.Recoverable, result.Outcome);
        Assert.Equal(ConstructionRecoveryKind.Npc, result.Recovery);
        Assert.True(result.ShouldOpenBuildPage);
    }

    [Theory]
    [InlineData(102, true)]
    [InlineData(120, false)]
    public void Evaluate_NpcDoesNotNavigateWhenALocalGoldGateFails(int gold, bool dailyBudgetAvailable)
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (2000, 2000, 0, 0),
            production: (100, 100, 100, 100),
            heroEnabled: false,
            npcEnabled: true,
            gold: gold,
            goldLimit: 100,
            dailyBudgetAvailable: dailyBudgetAvailable));

        Assert.Equal(ConstructionAffordabilityOutcome.Blocked, result.Outcome);
        Assert.False(result.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_NpcRespectsBuildTimePolicyBeforeNavigation()
    {
        var request = Request(
            stock: (900, 2000, 1000, 1000),
            production: (600, 100, 100, 100),
            heroEnabled: false,
            npcEnabled: true,
            gold: 120,
            dailyBudgetAvailable: true);

        var result = ConstructionAffordabilityPlanner.Evaluate(request with
        {
            Npc = request.Npc with { BuildTimeLimitEnabled = true, BuildTimeLimitSeconds = 900 },
        });

        Assert.Equal(ConstructionAffordabilityOutcome.Blocked, result.Outcome);
        Assert.Equal(Now.AddMinutes(10), result.NextAttemptAtUtc);
        Assert.False(result.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_IncompleteOrNonLiveSnapshot_RemainsUnknownAndKeepsLiveFallback()
    {
        var request = Request(stock: (1000, 1000, 1000, 1000), production: (100, 100, 100, 100));

        var incomplete = ConstructionAffordabilityPlanner.Evaluate(request with
        {
            Village = request.Village with { WarehouseCapacity = null },
        });
        var restored = ConstructionAffordabilityPlanner.Evaluate(request with
        {
            Village = request.Village with { IsLive = false },
        });

        Assert.Equal(ConstructionAffordabilityOutcome.Unknown, incomplete.Outcome);
        Assert.Equal(ConstructionAffordabilityOutcome.Unknown, restored.Outcome);
        Assert.True(incomplete.ShouldOpenBuildPage);
        Assert.True(restored.ShouldOpenBuildPage);
    }

    [Fact]
    public void Evaluate_IncompleteProduction_RemainsUnknownAndCannotApproveNpcRecovery()
    {
        var request = Request(
            stock: (2000, 2000, 0, 0),
            production: (100, 100, null, 100),
            heroEnabled: false,
            npcEnabled: true,
            gold: 120,
            dailyBudgetAvailable: true);

        var result = ConstructionAffordabilityPlanner.Evaluate(request);

        Assert.Equal(ConstructionAffordabilityOutcome.Unknown, result.Outcome);
        Assert.True(result.ShouldOpenBuildPage);
        Assert.Equal(ConstructionRecoveryKind.None, result.Recovery);
    }

    [Fact]
    public void Evaluate_ProductionDeadlineWinsWhenEarlierThanHeroRevalidation()
    {
        var result = ConstructionAffordabilityPlanner.Evaluate(Request(
            stock: (900, 1000, 1000, 1000),
            production: (600, 100, 100, 100),
            hero: new ConstructionResourceAmounts(0, 0, 0, 0),
            heroNextProbeAt: Now.AddMinutes(20)));

        Assert.Equal(ConstructionAffordabilityOutcome.Blocked, result.Outcome);
        Assert.Equal(Now.AddMinutes(10), result.NextAttemptAtUtc);
    }

    private static ConstructionAffordabilityRequest Request(
        (long Wood, long Clay, long Iron, long Crop) stock,
        (double? Wood, double? Clay, double? Iron, double? Crop) production,
        ConstructionResourceAmounts? hero = null,
        DateTimeOffset? heroNextProbeAt = null,
        bool heroEnabled = true,
        bool npcEnabled = false,
        int? gold = null,
        int goldLimit = 100,
        bool dailyBudgetAvailable = false)
    {
        return new ConstructionAffordabilityRequest(
            new ConstructionResourceAmounts(1000, 1000, 1000, 1000),
            new ConstructionVillageResourceSnapshot(
                new ConstructionResourceAmounts(stock.Wood, stock.Clay, stock.Iron, stock.Crop),
                new ConstructionProductionRates(production.Wood, production.Clay, production.Iron, production.Crop),
                WarehouseCapacity: 10_000,
                GranaryCapacity: 10_000,
                IsLive: true),
            new ConstructionHeroRecoveryPolicy(
                Enabled: heroEnabled,
                Inventory: hero,
                MaxUseEnabled: true,
                MaxUsePerResource: 5000,
                NextRevalidationAtUtc: heroNextProbeAt),
            new ConstructionNpcRecoveryPolicy(
                Enabled: npcEnabled,
                AllowGoldSpending: npcEnabled,
                Gold: gold,
                GoldLimit: goldLimit,
                GoldCost: 3,
                DailyBudgetAvailable: dailyBudgetAvailable,
                BuildTimeLimitEnabled: false,
                BuildTimeLimitSeconds: 0),
            Now,
            TimeSpan.FromMinutes(15));
    }
}
