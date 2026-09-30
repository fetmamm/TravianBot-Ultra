using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class CropShortageRecoveryPlannerTests
{
    [Theory]
    [InlineData(-10, 2, 10, false)]
    [InlineData(1, 2, 10, false)]
    [InlineData(2, 2, 1, true)]
    [InlineData(-10, null, 1, false)]
    [InlineData(-10, null, 2, true)]
    public void ShouldResumeParent_UsesRequiredUpkeepOrCompletedFallbackBatch(
        double cropProduction,
        int? requiredFreeCrop,
        int completedSteps,
        bool expected)
    {
        Assert.Equal(
            expected,
            CropShortageRecoveryPlanner.ShouldResumeParent(cropProduction, requiredFreeCrop, completedSteps));
    }

    [Fact]
    public void Plan_SelectsTwoLowestCroplandsByLevelThenSlot()
    {
        var status = Status(
            new ResourceField(1, "crop", "Cropland", 4, null),
            new ResourceField(2, "crop", "Cropland", 2, null),
            new ResourceField(3, "crop", "Cropland", 2, null),
            new ResourceField(4, "wood", "Woodcutter", 1, null));

        var plan = CropShortageRecoveryPlanner.Plan(status, [], maxLevel: 10);

        Assert.Collection(
            plan.Steps,
            first => { Assert.Equal(2, first.SlotId); Assert.Equal(3, first.TargetLevel); },
            second => { Assert.Equal(3, second.SlotId); Assert.Equal(3, second.TargetLevel); });
        Assert.Equal(-12, plan.CropProductionPerHour);
    }

    [Fact]
    public void Plan_CountsActiveCroplandTowardTwoStepLimit()
    {
        var status = Status(
            new ResourceField(1, "crop", "Cropland", 2, null),
            new ResourceField(2, "crop", "Cropland", 3, null)) with
        {
            ActiveConstructions =
            [
                new ActiveConstruction(ConstructionKind.Resource, "Cropland", 3, 60, null, SlotId: 1),
            ],
        };

        var plan = CropShortageRecoveryPlanner.Plan(status, [], maxLevel: 10);

        Assert.Equal(1, plan.ActiveCroplandCount);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(2, step.SlotId);
    }

    [Fact]
    public void Plan_ReusesExactPendingUpgradeAndDetectsAllMax()
    {
        var status = Status(
            new ResourceField(1, "crop", "Cropland", 9, null),
            new ResourceField(2, "crop", "Cropland", 10, null));
        var existing = new QueueItem
        {
            TaskName = "upgrade_resource_to_level",
            Payload = new ResourceUpgradePayload(1, 10, "Cropland").ToDictionary(),
            Status = QueueStatus.Pending,
        };

        var plan = CropShortageRecoveryPlanner.Plan(status, [existing], maxLevel: 10);
        Assert.Equal(existing.Id, Assert.Single(plan.Steps).ExistingQueueItemId);

        var maxPlan = CropShortageRecoveryPlanner.Plan(
            Status(
                new ResourceField(1, "crop", "Cropland", 10, null),
                new ResourceField(2, "crop", "Cropland", 10, null)),
            [],
            maxLevel: 10);
        Assert.True(maxPlan.AllCroplandsAtMax);
        Assert.Empty(maxPlan.Steps);
    }

    private static VillageStatus Status(params ResourceField[] fields) => new(
        "Village",
        [],
        new Dictionary<string, string>(),
        fields,
        [],
        [],
        IsCapital: false,
        ResourceStorageForecasts:
        [
            new ResourceStorageForecast("crop", 100, 800, 12.5, -12, null),
        ],
        ActiveConstructions: []);
}
