using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionStartDelayPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolve_FreePlusSlotWithRunningBuild_PreparesDelayBeforeNavigation()
    {
        var item = PendingConstruction();
        var status = StatusWithActiveBuild(seconds: 1_000);
        var options = new BotOptions
        {
            ConstructionHumanizeDelayEnabled = true,
            ConstructionHumanizeQueuePercentMin = 10,
            ConstructionHumanizeQueuePercentMax = 20,
            ConstructionHumanizeMaxDelayMinutes = 25,
        };

        var result = ConstructionStartDelayPlanner.Resolve(
            item,
            status,
            travianPlusActive: true,
            options,
            Now,
            (_, _) => 15);

        Assert.NotNull(result);
        Assert.Equal(150, result.DelaySeconds);
        Assert.Equal(Now.AddSeconds(1_000), result.ReferenceFinishUtc);
        Assert.Equal(Now.AddSeconds(150), result.ReadyAtUtc);
    }

    [Theory]
    [InlineData(BotOptionPayloadKeys.ConstructionLoginFill)]
    [InlineData(BotOptionPayloadKeys.ConstructionPreSleepFill)]
    [InlineData(BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied)]
    public void Resolve_ExplicitFillOrPreparedDelay_DoesNotScheduleAgain(string key)
    {
        var item = PendingConstruction();
        item.Payload[key] = "true";

        var result = ConstructionStartDelayPlanner.Resolve(
            item,
            StatusWithActiveBuild(1_000),
            travianPlusActive: true,
            new BotOptions { ConstructionHumanizeDelayEnabled = true },
            Now,
            (_, _) => 15);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_AlreadyConsumedHumanizeDefer_DoesNotRandomizeAgain()
    {
        var item = PendingConstruction();
        item.Payload[BotOptionPayloadKeys.UpgradeDeferReason] = BotOptionPayloadKeys.UpgradeDeferReasonHumanize;
        var randomCalls = 0;

        var result = ConstructionStartDelayPlanner.Resolve(
            item,
            StatusWithActiveBuild(1_000),
            travianPlusActive: true,
            new BotOptions { ConstructionHumanizeDelayEnabled = true },
            Now,
            (_, _) =>
            {
                randomCalls++;
                return 15;
            });

        Assert.Null(result);
        Assert.Equal(0, randomCalls);
    }

    [Fact]
    public void ResolveAfterFullQueue_PlusUsesTheBuildContinuingAfterFirstFinish()
    {
        var status = StatusWithActiveBuild(1_000) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Building, "Main Building", 4, 1_000, null,
                    TimerSnapshot.FromRemaining(1_000, Now)),
                new ActiveConstruction(ConstructionKind.Building, "Warehouse", 2, 1_600, null,
                    TimerSnapshot.FromRemaining(1_600, Now)),
            ],
        };
        var result = ConstructionStartDelayPlanner.ResolveAfterFullQueue(
            PendingConstruction(), status, true,
            new BotOptions
            {
                ConstructionHumanizeDelayEnabled = true,
                ConstructionHumanizeQueuePercentMin = 10,
                ConstructionHumanizeQueuePercentMax = 20,
                ConstructionHumanizeMaxDelayMinutes = 25,
            },
            Now, (_, _) => 15);

        Assert.NotNull(result);
        Assert.Equal(1_091, result.QueueRetrySeconds);
        Assert.Equal(91, result.HumanizeExtraSeconds);
        Assert.Equal(Now.AddSeconds(1_000), result.ReferenceFinishUtc);
        Assert.Equal(Now.AddSeconds(1_091), result.ReadyAtUtc);
    }

    [Fact]
    public void ResolveAfterFullQueue_NoPlusWaitsPastTheFinish()
    {
        var result = ConstructionStartDelayPlanner.ResolveAfterFullQueue(
            PendingConstruction(), StatusWithActiveBuild(1_000), false,
            new BotOptions
            {
                ConstructionHumanizeDelayEnabled = true,
                ConstructionHumanizeNoPlusMinMinutes = 10,
                ConstructionHumanizeNoPlusMaxMinutes = 20,
            },
            Now, (_, _) => 10);

        Assert.NotNull(result);
        Assert.Equal(1_601, result.QueueRetrySeconds);
        Assert.Equal(601, result.HumanizeExtraSeconds);
    }

    [Fact]
    public void ResolveAfterFullQueue_UnknownSnapshotDoesNotRandomize()
    {
        var randomCalls = 0;
        var result = ConstructionStartDelayPlanner.ResolveAfterFullQueue(
            PendingConstruction(), null, false,
            new BotOptions { ConstructionHumanizeDelayEnabled = true },
            Now, (_, _) => { randomCalls++; return 10; });

        Assert.Null(result);
        Assert.Equal(0, randomCalls);
    }

    [Fact]
    public void ResolveAfterFullQueue_UnconfirmedOverviewDoesNotPersistATimer()
    {
        var status = StatusWithActiveBuild(1_000) with { ActiveConstructionsFromOverview = false };

        var result = ConstructionStartDelayPlanner.ResolveAfterFullQueue(
            PendingConstruction(), status, false,
            new BotOptions { ConstructionHumanizeDelayEnabled = true }, Now, (_, _) => 10);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveAfterFullQueue_PreparedDelayDoesNotRandomizeAgain()
    {
        var item = PendingConstruction();
        item.Payload[BotOptionPayloadKeys.ConstructionHumanizePreNavigationDelaySatisfied] = "true";
        var randomCalls = 0;

        var result = ConstructionStartDelayPlanner.ResolveAfterFullQueue(
            item, StatusWithActiveBuild(1_000), false,
            new BotOptions { ConstructionHumanizeDelayEnabled = true }, Now,
            (_, _) => { randomCalls++; return 10; });

        Assert.Null(result);
        Assert.Equal(0, randomCalls);
    }

    private static QueueItem PendingConstruction() => new()
    {
        TaskName = "upgrade_building_to_level",
        Group = QueueGroup.Construction,
        Status = QueueStatus.Pending,
        NextAttemptAt = Now,
    };

    private static VillageStatus StatusWithActiveBuild(int seconds) => new(
        ActiveVillage: "G01",
        Villages: [],
        Resources: new Dictionary<string, string>(),
        ResourceFields: [],
        Buildings: [],
        BuildQueue: [],
        Tribe: "Gauls",
        ActiveConstructions:
        [
            new ActiveConstruction(
                ConstructionKind.Building,
                "Main Building",
                4,
                seconds,
                null,
                TimerSnapshot.FromRemaining(seconds, Now)),
        ],
        ActiveConstructionsFromOverview: true);
}
