using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmTargetProtectionContextTests
{
    [Theory]
    [InlineData("Owner", "Other", FarmTargetProtectionDecision.ExcludedPlayer)]
    [InlineData("Other", "My Alliance", FarmTargetProtectionDecision.ExcludedAlliance)]
    [InlineData("Blocked Player", null, FarmTargetProtectionDecision.ExcludedPlayer)]
    [InlineData("Other", "Blocked Alliance", FarmTargetProtectionDecision.ExcludedAlliance)]
    [InlineData("Allowed", null, FarmTargetProtectionDecision.Allowed)]
    public void Evaluate_ProtectsOwnAndConfiguredTargets(
        string player,
        string? alliance,
        FarmTargetProtectionDecision expected)
    {
        var context = new FarmTargetProtectionContext(
            " owner ",
            "my   alliance",
            excludeOwnAlliance: true,
            ["blocked player"],
            ["blocked alliance"]);

        var actual = context.Evaluate(
            isOasis: false,
            new FarmTargetIdentity(true, player, alliance));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Evaluate_AllowsVerifiedUnoccupiedOasisButRejectsOwnerlessVillage()
    {
        var context = new FarmTargetProtectionContext("Owner", null, false, [], []);
        var unowned = new FarmTargetIdentity(true, null, null);

        Assert.Equal(FarmTargetProtectionDecision.Allowed, context.Evaluate(true, unowned));
        Assert.Equal(FarmTargetProtectionDecision.IdentityUnavailable, context.Evaluate(false, unowned));
    }

    [Fact]
    public void EvaluateAndCache_ReusesOnlyResolvedDecisions()
    {
        var context = new FarmTargetProtectionContext("Owner", null, false, ["Blocked"], []);

        Assert.Equal(
            FarmTargetProtectionDecision.IdentityUnavailable,
            context.EvaluateAndCache(1, 2, false, new FarmTargetIdentity(false, null, null)));
        Assert.False(context.TryGetCachedDecision(1, 2, out _));

        Assert.Equal(
            FarmTargetProtectionDecision.ExcludedPlayer,
            context.EvaluateAndCache(1, 2, false, new FarmTargetIdentity(true, "blocked", null)));
        Assert.True(context.TryGetCachedDecision(1, 2, out var cached));
        Assert.Equal(FarmTargetProtectionDecision.ExcludedPlayer, cached);
    }
}
