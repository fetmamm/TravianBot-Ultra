using TbotUltra.Core.Configuration;
using TbotUltra.Core.Construction;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionAffordabilityOperationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_UnchangedResourcesPreserveFutureDeadline()
    {
        var operation = Operation();
        var stateKey = Guid.NewGuid().ToString("N");

        var first = Evaluate(operation, stateKey, Now, wood: 100, woodProduction: 100);
        var unchanged = Evaluate(operation, stateKey, Now.AddMinutes(1), wood: 100, woodProduction: 100);

        Assert.Equal(Now.AddMinutes(15), first.NextAttemptAtUtc);
        Assert.Equal(first.NextAttemptAtUtc, unchanged.NextAttemptAtUtc);
    }

    [Fact]
    public void Evaluate_ActualResourceIncreaseMayAdvanceDeadline()
    {
        var operation = Operation();
        var stateKey = Guid.NewGuid().ToString("N");

        var first = Evaluate(operation, stateKey, Now, wood: 100, woodProduction: 100);
        var increased = Evaluate(operation, stateKey, Now.AddMinutes(2), wood: 900, woodProduction: 600);

        Assert.Equal(Now.AddMinutes(15), first.NextAttemptAtUtc);
        Assert.Equal(Now.AddMinutes(12), increased.NextAttemptAtUtc);
    }

    private static ConstructionAffordabilityDecision Evaluate(
        ConstructionAffordabilityOperation operation,
        string stateKey,
        DateTimeOffset now,
        long wood,
        double woodProduction)
    {
        return operation.Evaluate(
            new BuildingLevelStats(1, 1000, 0, 0, 0, 0, 0, 60),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = wood.ToString(),
                ["clay"] = "1000",
                ["iron"] = "1000",
                ["crop"] = "1000",
            },
            new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wood"] = woodProduction,
                ["clay"] = 100,
                ["iron"] = 100,
                ["crop"] = 100,
            },
            warehouseCapacity: 10_000,
            granaryCapacity: 10_000,
            isLive: true,
            heroSnapshot: null,
            gold: null,
            dailyBudgetAvailable: false,
            stateKey,
            now);
    }

    private static ConstructionAffordabilityOperation Operation() => new(new BotOptions
    {
        HeroResourceTransferEnabled = false,
        HeroResourceUseConstruction = false,
        NpcTradeConstructionEnabled = false,
        AllowGoldSpending = false,
    });
}
