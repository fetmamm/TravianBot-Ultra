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
}
