using TbotUltra.Core.Travian;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ExpansionCalculatorTests
{
    [Theory]
    [InlineData(ExpansionBuildingType.Residence, 9, 0)]
    [InlineData(ExpansionBuildingType.Residence, 10, 1)]
    [InlineData(ExpansionBuildingType.Residence, 19, 1)]
    [InlineData(ExpansionBuildingType.Residence, 20, 2)]
    [InlineData(ExpansionBuildingType.Palace, 10, 1)]
    [InlineData(ExpansionBuildingType.Palace, 15, 2)]
    [InlineData(ExpansionBuildingType.Palace, 20, 3)]
    [InlineData(ExpansionBuildingType.CommandCenter, 15, 2)]
    public void ExpansionSlots_FollowBuildingLevel(ExpansionBuildingType building, int level, int expected)
    {
        Assert.Equal(expected, ExpansionCalculator.ExpansionSlots(building, level));
    }

    [Fact]
    public void PlanSettlerTraining_TrainsThreeForOneFreeSlot()
    {
        var plan = ExpansionCalculator.PlanSettlerTraining(ExpansionBuildingType.Residence, 10, 0, 0, 0, 0);

        Assert.Equal(1, plan.FreeSlots);
        Assert.Equal(3, plan.SettlersToTrain);
    }

    [Fact]
    public void PlanSettlerTraining_CountsSettlersAtHomeInTrainingAndOnTheWay()
    {
        var plan = ExpansionCalculator.PlanSettlerTraining(ExpansionBuildingType.Palace, 15, 0, 2, 1, 3);

        Assert.Equal(2, plan.FreeSlots);
        Assert.Equal(6, plan.SettlersAvailable);
        Assert.Equal(0, plan.SettlersToTrain);
    }

    [Fact]
    public void PlanSettlerTraining_TrainsNothingBelowLevelTen()
    {
        var plan = ExpansionCalculator.PlanSettlerTraining(ExpansionBuildingType.Residence, 9, 0, 0, 0, 0);

        Assert.Equal(0, plan.SettlersToTrain);
        Assert.Contains("needs level 10", plan.Reason);
    }

    [Fact]
    public void PlanSettlerTraining_TrainsNothingWhenSlotsAreUsed()
    {
        var plan = ExpansionCalculator.PlanSettlerTraining(ExpansionBuildingType.Residence, 10, 1, 0, 0, 0);

        Assert.Equal(0, plan.FreeSlots);
        Assert.Equal(0, plan.SettlersToTrain);
    }

    [Theory]
    [InlineData(ExpansionBuildingType.Residence, 9, 10)]
    [InlineData(ExpansionBuildingType.Residence, 18, 20)]
    [InlineData(ExpansionBuildingType.Residence, 20, null)]
    [InlineData(ExpansionBuildingType.Palace, 10, 15)]
    public void NextSlotLevel_IsTheNextUnlockingLevel(ExpansionBuildingType building, int level, int? expected)
    {
        Assert.Equal(expected, ExpansionCalculator.NextSlotLevel(building, level));
    }

    [Fact]
    public void ParseSettlementSlots_ReadsTheFooterFraction()
    {
        Assert.Equal((5, 5), ExpansionCalculator.ParseSettlementSlots("‭‭5‬/‭5‬‬"));
        Assert.Null(ExpansionCalculator.ParseSettlementSlots(""));
    }

    [Fact]
    public void ParseCulturePointsProgress_IgnoresTextSeparatorsAndBidiMarkers()
    {
        var progress = ExpansionCalculator.ParseCulturePointsProgress("Nächster Siedlungs-Slot: ‭‭5.091‬ / ‭6.500‬‬ Kulturpunkte");

        Assert.Equal((5091L, 6500L), progress);
    }

    [Fact]
    public void SecondsUntilCulturePoints_UsesTheDailyProduction()
    {
        Assert.Equal(43200, ExpansionCalculator.SecondsUntilCulturePoints(500, 1000, 1000));
        Assert.Equal(0, ExpansionCalculator.SecondsUntilCulturePoints(1000, 1000, 1000));
        Assert.Null(ExpansionCalculator.SecondsUntilCulturePoints(500, 1000, 0));
    }

    [Fact]
    public void EvaluateFounding_ReadyWhenEveryRequirementIsMet()
    {
        var readiness = ExpansionCalculator.EvaluateFounding(State());

        Assert.True(readiness.IsReady);
        Assert.Empty(readiness.MissingRequirements);
    }

    [Fact]
    public void EvaluateFounding_ListsEveryMissingRequirement()
    {
        var readiness = ExpansionCalculator.EvaluateFounding(State() with
        {
            SettlersAtHome = 2,
            FreeExpansionSlots = 0,
            CulturePoints = 400,
            Resources = Resources(clay: 749),
            TargetFieldIsFree = false,
        });

        Assert.False(readiness.IsReady);
        Assert.Equal(
            ["settlers at home 2/3", "no free expansion slot", "culture points 400/500", "clay 749/750", "target field is not a free village field"],
            readiness.MissingRequirements);
    }

    [Fact]
    public void SecondsUntilAffordable_UsesTheSlowestMissingResource()
    {
        var stock = new Dictionary<string, long> { ["wood"] = 4800, ["clay"] = 4400, ["iron"] = 9000, ["crop"] = 4200 };
        var production = new Dictionary<string, double?> { ["wood"] = 3600, ["clay"] = 3600, ["iron"] = 3600, ["crop"] = 1800 };
        var cost = new Dictionary<string, long> { ["wood"] = 5800, ["clay"] = 4400, ["iron"] = 4600, ["crop"] = 5200 };

        Assert.Equal(2000, ExpansionCalculator.SecondsUntilAffordable(stock, production, cost));
    }

    [Fact]
    public void SecondsUntilAffordable_IsUnknownWithoutProductionForAMissingResource()
    {
        var stock = new Dictionary<string, long> { ["wood"] = 0 };
        var production = new Dictionary<string, double?> { ["wood"] = null };
        var cost = new Dictionary<string, long> { ["wood"] = 5800 };

        Assert.Null(ExpansionCalculator.SecondsUntilAffordable(stock, production, cost));
    }

    private static VillageFoundingState State() => new(3, 1, 500, 500, Resources(), true);

    private static IReadOnlyDictionary<string, long> Resources(long clay = 750)
        => new Dictionary<string, long> { ["wood"] = 750, ["clay"] = clay, ["iron"] = 750, ["crop"] = 750 };
}

public sealed class ExpansionWorkerHelperTests
{
    [Theory]
    [InlineData("xy:-133|132", -133, 132)]
    [InlineData("(-133|132)", -133, 132)]
    [InlineData("-133/132", -133, 132)]
    public void TryParseFoundingTarget_ReadsCoordinates(string value, int x, int y)
    {
        Assert.True(TbotUltra.Worker.Services.BotTaskRunner.TryParseFoundingTarget(value, out var parsedX, out var parsedY, out var tribeId));
        Assert.Equal((x, y), (parsedX, parsedY));
        Assert.Null(tribeId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Residence")]
    public void TryParseFoundingTarget_RejectsOtherValues(string value)
    {
        Assert.False(TbotUltra.Worker.Services.BotTaskRunner.TryParseFoundingTarget(value, out _, out _, out _));
    }

    [Fact]
    public void TryParseFoundingTarget_ReadsTheTribe()
    {
        Assert.True(TbotUltra.Worker.Services.BotTaskRunner.TryParseFoundingTarget("xy:-133|132;tribe:2", out var x, out var y, out var tribeId));
        Assert.Equal((-133, 132, 2), (x, y, tribeId));
    }

    [Fact]
    public void SettlerTrainingSecondsLeft_UsesTheLastSettlerRow()
    {
        var queue = new[]
        {
            new TbotUltra.Worker.Domain.BuildQueueItem("1 Siedler", "0:12:42"),
            new TbotUltra.Worker.Domain.BuildQueueItem("5 Clubswingers", "1:00:00"),
            new TbotUltra.Worker.Domain.BuildQueueItem("2 Settlers", "0:52:42"),
        };

        Assert.Equal(3162, TbotUltra.Worker.Services.TravianClient.SettlerTrainingSecondsLeft(queue));
        Assert.Null(TbotUltra.Worker.Services.TravianClient.SettlerTrainingSecondsLeft([]));
    }

    [Fact]
    public void CountSettlersInQueue_SumsSettlerRows()
    {
        var queue = new[]
        {
            new TbotUltra.Worker.Domain.BuildQueueItem("2 Settlers", "0:10:00"),
            new TbotUltra.Worker.Domain.BuildQueueItem("1 Settler", "0:20:00"),
            new TbotUltra.Worker.Domain.BuildQueueItem("5 Clubswingers", "0:05:00"),
            new TbotUltra.Worker.Domain.BuildQueueItem("1 Siedler", "0:20:00"),
        };

        Assert.Equal(4, TbotUltra.Worker.Services.TravianClient.CountSettlersInQueue(queue));
    }
}
