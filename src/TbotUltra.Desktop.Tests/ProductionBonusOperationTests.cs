using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProductionBonusOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tbot-ultra-production-operation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Apply_ObservedBatchPersistsTypedDeadlines()
    {
        var now = new DateTimeOffset(2026, 9, 27, 6, 30, 0, TimeSpan.Zero);
        var outcome = ProductionBonusOutcome.Observed(
            "batch complete",
            [
                new ProductionBonusResourceState(
                    "lumber",
                    15,
                    3600,
                    ProductionBonusNextAttemptKind.DailyReset,
                    0,
                    false),
                new ProductionBonusResourceState(
                    "clay",
                    0,
                    0,
                    ProductionBonusNextAttemptKind.RelativeDelay,
                    ProductionBonusDomParser.CooldownRetrySeconds,
                    false),
            ],
            TimeSpan.FromHours(2),
            freeVideoAvailable: true,
            attemptedResources: ["lumber", "clay"],
            unconfirmedResources: ["clay"]);

        var result = ProductionBonusOperation.Apply(
            _root,
            "alice",
            outcome,
            now,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10),
            dailyResetHour: 9);

        Assert.Equal(ProductionBonusApplicationStatus.Applied, result.Status);
        Assert.True(result.StateChanged);
        Assert.Equal(2, result.Timers.Count);
        Assert.Equal(result.Timers.Min(timer => timer.NextAttemptAtUtc), result.NextDeadlineUtc);
        Assert.Equal(2, ProductionBonusStateStore.Load(_root, "alice").Count);
    }

    [Fact]
    public void Apply_DeferredOutcomePreservesPersistedTimers()
    {
        var existing = new ProductionBonusResourceTimer(
            "iron",
            15,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddHours(2));
        ProductionBonusStateStore.Save(_root, "alice", [existing]);
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(15);

        var result = ProductionBonusOperation.Apply(
            _root,
            "alice",
            ProductionBonusOutcome.Deferred("cooldown", retryAt),
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            TimeSpan.Zero,
            dailyResetHour: 9);

        Assert.Equal(ProductionBonusApplicationStatus.Deferred, result.Status);
        Assert.False(result.StateChanged);
        Assert.Equal(retryAt, result.NextDeadlineUtc);
        Assert.Equal(existing, Assert.Single(ProductionBonusStateStore.Load(_root, "alice")));
    }

    [Fact]
    public void Apply_FailedOutcomeCreatesTypedBackoffOnlyWhenStateIsEmpty()
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        var result = ProductionBonusOperation.Apply(
            _root,
            "alice",
            ProductionBonusOutcome.Failed("inspection failed"),
            now,
            TimeSpan.Zero,
            TimeSpan.Zero,
            dailyResetHour: 9);

        Assert.Equal(ProductionBonusApplicationStatus.Failed, result.Status);
        Assert.True(result.StateChanged);
        Assert.Equal(now.AddMinutes(30), result.NextDeadlineUtc);
        Assert.Equal(4, ProductionBonusStateStore.Load(_root, "alice").Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
