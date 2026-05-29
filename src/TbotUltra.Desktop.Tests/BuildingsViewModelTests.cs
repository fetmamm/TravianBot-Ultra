using System;
using System.Linq;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingsViewModelTests
{
    [Fact]
    public void DescribeLoadedSlots_CountsOccupiedAndFree()
    {
        var vm = new BuildingsViewModel();
        vm.BuildingSlots.Add(new BuildingSlotRow { SlotId = 1, Name = "Main Building", Level = 5 });
        vm.BuildingSlots.Add(new BuildingSlotRow { SlotId = 2, Name = "Empty" });
        vm.BuildingSlots.Add(new BuildingSlotRow { SlotId = 3, Name = "Warehouse", Level = 3 });

        var text = vm.DescribeLoadedSlots("active village 'Capital'");

        Assert.Equal(
            "Buildings loaded for active village 'Capital'. Occupied slots: 2, free slots: 1.",
            text);
    }

    [Fact]
    public void DescribeLoadedSlots_NoSlots_ReportsAllFree()
    {
        var vm = new BuildingsViewModel();

        var text = vm.DescribeLoadedSlots("selected village 'New'");

        Assert.Equal(
            "Buildings loaded for selected village 'New'. Occupied slots: 0, free slots: 0.",
            text);
    }

    [Fact]
    public void CreateBuildingSlotLayout_CoversAllSlots_WithRoundedCoordinates()
    {
        var layout = BuildingsViewModel.CreateBuildingSlotLayout();

        Assert.Equal(22, layout.Count);
        Assert.Equal(Enumerable.Range(19, 22), layout.Keys.OrderBy(id => id));
        foreach (var (left, top) in layout.Values)
        {
            Assert.Equal(left, Math.Round(left, 1));
            Assert.Equal(top, Math.Round(top, 1));
        }
    }

    [Theory]
    [InlineData(26, true)]
    [InlineData(39, true)]
    [InlineData(40, true)]
    [InlineData(19, false)]
    [InlineData(25, false)]
    public void IsPinnedBuildingTopSlot_MatchesPinnedSlots(int slotId, bool expected)
    {
        Assert.Equal(expected, BuildingsViewModel.IsPinnedBuildingTopSlot(slotId));
    }
}
