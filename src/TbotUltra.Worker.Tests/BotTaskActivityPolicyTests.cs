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
    public void DeferredConstructionActivity_CountsMutationsCompletedBeforeQueueFilled()
    {
        var results = new[]
        {
            new BotTaskResult(
                "upgrade_all_resources_to_level",
                "Resource slot 6: build queue full. Deferring upgrade. Upgrades performed: 2. queue_wait_seconds=29",
                ConstructionTaskOutcome.WaitingOrBlocked),
        };

        Assert.Equal(2, BotTaskRunner.CountVerifiedActivities(
            "upgrade_all_resources_to_level",
            results,
            handlerCompleted: false,
            waitReasonCode: null));
    }

    [Fact]
    public void CompletedBulkResourceActivity_CountsEveryReportedUpgrade()
    {
        var results = new[]
        {
            new BotTaskResult(
                "upgrade_all_resources_to_level",
                "All selected resource fields are at or above target level 2. Upgrades made: 2.",
                ConstructionTaskOutcome.AlreadySatisfied),
        };

        Assert.Equal(2, BotTaskRunner.CountVerifiedActivities(
            "upgrade_all_resources_to_level",
            results,
            handlerCompleted: true,
            waitReasonCode: null));
    }

    [Fact]
    public void ConstructionActivity_DoesNotCountPreviouslyQueuedUpgrade()
    {
        var results = new[]
        {
            new BotTaskResult(
                "upgrade_building_to_level",
                "Upgrade already queued and still in progress. Upgrades performed: 0. queue_wait_seconds=29",
                ConstructionTaskOutcome.QueuedOrInProgress),
        };

        Assert.Equal(0, BotTaskRunner.CountVerifiedActivities(
            "upgrade_building_to_level",
            results,
            handlerCompleted: false,
            waitReasonCode: TaskWaitReasons.WorkQueued));
    }

    [Fact]
    public void HeroAdventureActivity_IsPublishedSeparatelyAfterConfirmedDispatch()
    {
        var results = new[]
        {
            new BotTaskResult(
                "hero_adventure",
                "Actions: adventure_sent(top,duration=300s,return_eta=600s). queue_wait_seconds=600",
                ConstructionTaskOutcome.None),
        };

        Assert.Equal(
            ["hero_adventure"],
            BotTaskRunner.ResolveVerifiedActivityNames(
                "hero_manage",
                results,
                handlerCompleted: false,
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
