using System.Collections.Generic;
using System.Linq;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ResourcesViewModelTests
{
    [Fact]
    public void SetAllFields_StoresFlatListAndRebuildsGroupedColumns()
    {
        var vm = new ResourcesViewModel();
        var rows = new[]
        {
            new ResourceFieldRow { SlotId = 1, FieldType = "Woodcutter", Name = "Woodcutter", Level = 3 },
            new ResourceFieldRow { SlotId = 2, FieldType = "Clay Pit", Name = "Clay Pit", Level = 2 },
            new ResourceFieldRow { SlotId = 3, FieldType = "Iron Mine", Name = "Iron Mine", Level = 1 },
            new ResourceFieldRow { SlotId = 12, FieldType = "Cropland", Name = "Cropland", Level = 4 },
        };

        vm.SetAllFields(rows);

        Assert.Equal(4, vm.AllFields.Count);
        Assert.Single(vm.WoodFields);
        Assert.Single(vm.ClayFields);
        Assert.Single(vm.IronFields);
        Assert.Single(vm.CroplandFields);
    }

    [Fact]
    public void SetAllFields_ReplacesPreviousRows()
    {
        var vm = new ResourcesViewModel();
        vm.SetAllFields(new[] { new ResourceFieldRow { SlotId = 1, FieldType = "Woodcutter", Name = "Woodcutter", Level = 1 } });

        vm.SetAllFields(new[] { new ResourceFieldRow { SlotId = 2, FieldType = "Clay Pit", Name = "Clay Pit", Level = 1 } });

        Assert.Equal(new[] { 2 }, vm.AllFields.Select(row => row.SlotId));
        Assert.Empty(vm.WoodFields);
        Assert.Single(vm.ClayFields);
    }

    [Fact]
    public void ResolveQueuedResourceTarget_ReturnsQueuedTarget_WhenAboveCurrentLevel()
    {
        var vm = new ResourcesViewModel();
        var queued = new Dictionary<int, int> { [3] = 7 };

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, queued);

        Assert.Equal(7, target);
    }

    [Fact]
    public void ResolveQueuedResourceTarget_PrefersRememberedTarget_WhenHigherThanQueued()
    {
        var vm = new ResourcesViewModel();
        vm.RememberPendingTarget(3, 9);
        var queued = new Dictionary<int, int> { [3] = 7 };

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, queued);

        Assert.Equal(9, target);
    }

    [Fact]
    public void ResolveQueuedResourceTarget_NoQueuedTarget_ForgetsSlotAndReturnsNull()
    {
        var vm = new ResourcesViewModel();
        vm.RememberPendingTarget(3, 9);

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, new Dictionary<int, int>());

        Assert.Null(target);
        Assert.False(vm.TryGetPendingTarget(3, out _));
    }

    [Fact]
    public void ResolveQueuedResourceTarget_TargetReached_ForgetsSlotAndReturnsNull()
    {
        var vm = new ResourcesViewModel();
        var queued = new Dictionary<int, int> { [3] = 5 };

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, queued);

        Assert.Null(target);
        Assert.False(vm.TryGetPendingTarget(3, out _));
    }

    [Fact]
    public void TargetLevelOptions_CoverOneThroughTwenty_WithDefaultSelectionTen()
    {
        var vm = new ResourcesViewModel();

        Assert.Equal(Enumerable.Range(1, 20), vm.TargetLevelOptions);
        Assert.Equal(10, vm.SelectedTargetLevel);
    }

    [Fact]
    public void ActionsEnabled_DefaultsToTrue()
    {
        var vm = new ResourcesViewModel();

        Assert.True(vm.ActionsEnabled);
    }

    [Fact]
    public void ClearPendingTargets_RemovesAllRememberedTargets()
    {
        var vm = new ResourcesViewModel();
        vm.RememberPendingTarget(1, 4);
        vm.RememberPendingTarget(2, 6);

        vm.ClearPendingTargets();

        Assert.False(vm.TryGetPendingTarget(1, out _));
        Assert.False(vm.TryGetPendingTarget(2, out _));
    }

    [Fact]
    public void ApplyStorageForecasts_NegativeCropProduction_ShowsEmptyCountdownAndMarksCropStorage()
    {
        var vm = new ResourcesViewModel();
        var status = new VillageStatus(
            ActiveVillage: "Test",
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: [],
            BuildQueue: [],
            ResourceStorageForecasts:
            [
                new ResourceStorageForecast(
                    ResourceKey: "crop",
                    Current: 1200,
                    Capacity: 4000,
                    PercentOfCapacity: 30,
                    ProductionPerHour: -600,
                    SecondsToFull: null),
            ]);

        vm.ApplyStorageForecasts(status, renderImmediately: true);

        var crop = vm.StorageBars.Single(item => item.ResourceKey == "crop");
        Assert.True(crop.IsNegativeProduction);
        Assert.Equal("1 200 / 4 000", crop.CurrentMaxText);
        Assert.Equal("-600/h", crop.ProductionText);
        Assert.Equal("Empty in 2h 0m", crop.TimeUntilFullText);
        Assert.Contains("Time until empty: Empty in 2h 0m", crop.TooltipText);
    }
}
