using System.Collections.Generic;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ResourceFieldUpgradeEstimatorTests
{
    [Fact]
    public void TryEstimate_SumsEveryLevelForAllResourceFields()
    {
        var names = new[] { "Woodcutter", "Clay Pit", "Iron Mine", "Cropland" };
        var fields = new List<ResourceFieldRow>();
        for (var slotId = 1; slotId <= 18; slotId++)
        {
            fields.Add(new ResourceFieldRow
            {
                SlotId = slotId,
                Name = names[(slotId - 1) % names.Length],
                Level = slotId % 2 == 0 ? 2 : 1,
            });
        }

        var success = ResourceFieldUpgradeEstimator.TryEstimate(
            fields,
            targetLevel: 4,
            serverSpeed: 2,
            mainBuildingLevel: 5,
            out var estimate,
            out var failureReason);

        Assert.True(success, failureReason);

        double expectedSeconds = 0;
        long expectedWood = 0, expectedClay = 0, expectedIron = 0, expectedCrop = 0;
        foreach (var field in fields)
        {
            var gid = BuildingCatalogService.GidForName(field.Name)!.Value;
            for (var level = field.Level!.Value + 1; level <= 4; level++)
            {
                var stats = BuildingCatalogService.CostFor(gid, level)!;
                expectedSeconds += BuildingCatalogService.BuildSecondsFor(gid, level, 2, 5);
                expectedWood += stats.Wood;
                expectedClay += stats.Clay;
                expectedIron += stats.Iron;
                expectedCrop += stats.Crop;
            }
        }

        Assert.Equal(expectedSeconds, estimate.Seconds, 6);
        Assert.Equal(expectedWood, estimate.Wood);
        Assert.Equal(expectedClay, estimate.Clay);
        Assert.Equal(expectedIron, estimate.Iron);
        Assert.Equal(expectedCrop, estimate.Crop);
    }

    [Fact]
    public void TryEstimate_IncompleteFieldSnapshot_ReturnsNoEstimate()
    {
        var fields = new[]
        {
            new ResourceFieldRow { SlotId = 1, Name = "Woodcutter", Level = 1 },
        };

        var success = ResourceFieldUpgradeEstimator.TryEstimate(
            fields,
            targetLevel: 10,
            serverSpeed: 1,
            mainBuildingLevel: 1,
            out _,
            out var failureReason);

        Assert.False(success);
        Assert.Contains("expected 18 resource fields", failureReason);
    }
}
