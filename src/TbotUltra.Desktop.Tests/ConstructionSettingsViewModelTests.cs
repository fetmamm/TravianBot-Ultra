using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ConstructionSettingsViewModelTests
{
    [Fact]
    public void MainBuildingRebuild_IsEnabledAtLevelOneByDefault()
    {
        var vm = new ConstructionSettingsViewModel();

        Assert.True(vm.MainBuildingRebuildEnabled);
        Assert.Equal(1, vm.MainBuildingRebuildTargetLevel);
    }

    [Fact]
    public void MainBuildingRebuildTargetLevel_NormalizesToConfiguredRange()
    {
        var vm = new ConstructionSettingsViewModel { MainBuildingRebuildTargetLevel = int.MaxValue };

        Assert.Equal(ConstructionDefaults.MainBuildingRebuildTargetLevelMax, vm.MainBuildingRebuildTargetLevel);
    }

    [Fact]
    public void CropShortageRecovery_IsEnabledByDefault()
    {
        var vm = new ConstructionSettingsViewModel();

        Assert.True(vm.CropShortageRecoveryEnabled);
        Assert.True(ConstructionDefaults.CropShortageRecoveryEnabled);
    }

    [Fact]
    public void StorageUpgradeLevelsAhead_NormalizesToConfiguredRange()
    {
        var vm = new ConstructionSettingsViewModel
        {
            StorageUpgradeLevelsAhead = int.MaxValue,
        };

        Assert.Equal(ConstructionDefaults.StorageUpgradeLevelsAheadMax, vm.StorageUpgradeLevelsAhead);
    }
}
