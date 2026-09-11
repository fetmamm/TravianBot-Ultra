using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BotTaskActivityPolicyTests
{
    [Fact]
    public void ConstructionActivity_CountsEveryVerifiedMutation()
    {
        var results = new[]
        {
            new BotTaskResult("construct_building", "queued", ConstructionTaskOutcome.QueuedOrInProgress),
            new BotTaskResult("construct_building", "complete", ConstructionTaskOutcome.ConfirmedComplete),
            new BotTaskResult("construct_building", "already", ConstructionTaskOutcome.AlreadySatisfied),
        };

        Assert.Equal(2, BotTaskRunner.CountVerifiedActivities(
            "construct_building",
            results,
            handlerCompleted: true,
            waitReasonCode: null));
    }

    [Fact]
    public void ConstructionActivity_DoesNotCountAlreadySatisfiedOrBlocked()
    {
        var results = new[]
        {
            new BotTaskResult("upgrade_building_to_level", "already", ConstructionTaskOutcome.AlreadySatisfied),
            new BotTaskResult("upgrade_building_to_level", "blocked", ConstructionTaskOutcome.WaitingOrBlocked),
        };

        Assert.Equal(0, BotTaskRunner.CountVerifiedActivities(
            "upgrade_building_to_level",
            results,
            handlerCompleted: true,
            waitReasonCode: null));
    }

    [Fact]
    public void CompletedNonConstructionTask_CountsOneRun()
    {
        Assert.Equal(1, BotTaskRunner.CountVerifiedActivities(
            "collect_tasks",
            [],
            handlerCompleted: true,
            waitReasonCode: null));
    }

    [Fact]
    public void TypedWorkQueuedWithoutDetailedResult_CountsOneRun()
    {
        Assert.Equal(1, BotTaskRunner.CountVerifiedActivities(
            "build_troops",
            [],
            handlerCompleted: false,
            waitReasonCode: TaskWaitReasons.WorkQueued));
    }

    [Fact]
    public void OrdinaryWait_DoesNotCountAsActivity()
    {
        Assert.Equal(0, BotTaskRunner.CountVerifiedActivities(
            "hero_manage",
            [],
            handlerCompleted: false,
            waitReasonCode: TaskWaitReasons.HeroAway));
    }

    [Theory]
    [InlineData("Started demolition for Granary. queue_wait_seconds=60", ConstructionTaskOutcome.QueuedOrInProgress, 1)]
    [InlineData("Demolition already running for 60s. queue_wait_seconds=60", ConstructionTaskOutcome.AlreadySatisfied, 0)]
    public void DemolitionActivity_CountsOnlyNewlyStartedStep(
        string message,
        ConstructionTaskOutcome expectedOutcome,
        int expectedCount)
    {
        var result = new BotTaskResult(
            "demolish_building_to_level",
            message,
            BotTaskRunner.ClassifyConstructionTaskResult("demolish_building_to_level", message));

        Assert.Equal(expectedOutcome, result.ConstructionOutcome);
        Assert.Equal(expectedCount, BotTaskRunner.CountVerifiedActivities(
            "demolish_building_to_level",
            [result],
            handlerCompleted: false,
            waitReasonCode: TaskWaitReasons.WorkQueued));
    }
}
