using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class PreSleepFillHoldPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PendingStaleLoginFill_DoesNotHoldSleep()
    {
        var item = CreateItem(QueueStatus.Pending, BotOptionPayloadKeys.ConstructionLoginFill, Now);

        Assert.Equal(PreSleepFillHoldState.Ignore, PreSleepFillHoldPolicy.Evaluate(item, Now));
    }

    [Fact]
    public void DuePendingPreSleepFill_AwaitsDispatch()
    {
        var item = CreateItem(QueueStatus.Pending, BotOptionPayloadKeys.ConstructionPreSleepFill, Now.AddSeconds(20));

        Assert.Equal(PreSleepFillHoldState.AwaitingDispatch, PreSleepFillHoldPolicy.Evaluate(item, Now));
    }

    [Theory]
    [InlineData(BotOptionPayloadKeys.ConstructionPreSleepFill)]
    [InlineData(BotOptionPayloadKeys.ConstructionLoginFill)]
    public void RunningFill_HoldsUntilExecutionFinishes(string flag)
    {
        var item = CreateItem(QueueStatus.Running, flag, Now);

        Assert.Equal(PreSleepFillHoldState.Running, PreSleepFillHoldPolicy.Evaluate(item, Now));
    }

    private static QueueItem CreateItem(QueueStatus status, string flag, DateTimeOffset nextAttemptAt) => new()
    {
        Id = Guid.NewGuid(),
        Group = QueueGroup.Construction,
        TaskName = "upgrade_building_to_level",
        Status = status,
        NextAttemptAt = nextAttemptAt,
        Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [flag] = "true",
        },
    };
}
