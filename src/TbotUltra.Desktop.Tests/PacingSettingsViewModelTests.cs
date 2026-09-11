using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class PacingSettingsViewModelTests
{
    [Fact]
    public void SleepModes_AreMutuallyExclusiveButMayBothBeOff()
    {
        var vm = new PacingSettingsViewModel();

        vm.SmartSleepEnabled = true;
        Assert.False(vm.SessionPacingEnabled);
        vm.SmartSleepEnabled = false;

        Assert.False(vm.SessionPacingEnabled);
        Assert.False(vm.SmartSleepEnabled);
    }

    [Fact]
    public void ResetDefaults_RestoresEditablePacingValues()
    {
        var vm = new PacingSettingsViewModel
        {
            TaskMinSeconds = "9",
            TaskMaxSeconds = "10",
            FarmListStepDelayMinSeconds = "11",
            FarmListStepDelayMaxSeconds = "12",
            ShortVillageDeferSeconds = 90,
        };

        vm.ResetDefaults();

        Assert.Equal("0.8", vm.TaskMinSeconds);
        Assert.Equal("2", vm.TaskMaxSeconds);
        Assert.Equal("1", vm.FarmListStepDelayMinSeconds);
        Assert.Equal("4", vm.FarmListStepDelayMaxSeconds);
        Assert.Equal(60, vm.ShortVillageDeferSeconds);
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(45, 60)]
    public void ShortVillageDeferSeconds_AllowsOnlyDropdownChoices(int value, int expected)
    {
        var vm = new PacingSettingsViewModel { ShortVillageDeferSeconds = value };

        Assert.Equal(expected, vm.ShortVillageDeferSeconds);
    }

    [Fact]
    public void DisablingDorf2_ClearsDependentVillageScanSelections()
    {
        var vm = new PacingSettingsViewModel
        {
            VillageStatusSweepDorf2Enabled = true,
            VillageStatusSweepSmithyEnabled = true,
            VillageStatusSweepBarracksEnabled = true,
        };

        vm.VillageStatusSweepDorf2Enabled = false;

        Assert.False(vm.VillageStatusSweepDorf2DetailsEnabled);
        Assert.False(vm.VillageStatusSweepSmithyEnabled);
        Assert.False(vm.VillageStatusSweepBarracksEnabled);
    }

    [Fact]
    public void SessionAllowedHours_UsesStableHourValuesForPersistence()
    {
        var vm = new PacingSettingsViewModel();

        vm.SetSessionAllowedHours([0, 8, 23, 42]);

        Assert.Equal([0, 8, 23], vm.GetSelectedSessionHours());
        Assert.Equal("08:00", vm.SessionAllowedHours[8].Label);
    }
}
