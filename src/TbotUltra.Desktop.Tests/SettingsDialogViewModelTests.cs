using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SettingsDialogViewModelTests
{
    [Fact]
    public void Commands_ExposeConfiguredAvailabilityAndRaiseRequests()
    {
        var vm = new SettingsDialogViewModel(
            sleepNowEnabled: false,
            villageStatusSweepEnabled: true,
            dailyGoldSpendingResetEnabled: true,
            dailySilverSpendingResetEnabled: false);
        var saveRequested = false;
        var sweepRequested = false;
        var resetPacingRequested = false;
        vm.SaveRequested += () => saveRequested = true;
        vm.VillageStatusSweepNowRequested += () => sweepRequested = true;
        vm.ResetPacingRequested += () => resetPacingRequested = true;

        Assert.False(vm.SleepNowCommand.CanExecute(null));
        Assert.True(vm.RunVillageStatusSweepNowCommand.CanExecute(null));
        Assert.True(vm.ResetDailyGoldSpendingCommand.CanExecute(null));
        Assert.False(vm.ResetDailySilverSpendingCommand.CanExecute(null));

        vm.SaveCommand.Execute(null);
        vm.RunVillageStatusSweepNowCommand.Execute(null);
        vm.ResetPacingCommand.Execute(null);
        vm.SetVillageStatusSweepRunning(true);

        Assert.True(saveRequested);
        Assert.True(sweepRequested);
        Assert.True(resetPacingRequested);
        Assert.False(vm.RunVillageStatusSweepNowCommand.CanExecute(null));
    }

    [Fact]
    public void ChangeTracking_IgnoresProgrammaticChangesAndTracksUserChanges()
    {
        var vm = new SettingsDialogViewModel(true, true, true, true);

        using (vm.SuppressChangeTracking())
        {
            vm.MarkChanged();
        }

        Assert.False(vm.IsDirty);

        vm.MarkChanged();

        Assert.True(vm.IsDirty);
        vm.ResetChangeTracking();
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void GeneralSettings_DefaultToCurrentConfigurationDefaults()
    {
        var vm = new SettingsDialogViewModel(true, true, true, true);

        Assert.False(vm.DontNotifyNewVersion);
        Assert.True(vm.QuickReloginEnabled);
        Assert.True(vm.AutomaticallyCheckLanguage);
        Assert.False(vm.DetailedBrowserLoggingEnabled);
        Assert.False(vm.StartBrowserMinimized);
        Assert.True(vm.TurnOffVideoSound);
    }

    [Fact]
    public void SpendingLimits_ExposeEditableStateAndUsageProjection()
    {
        var vm = new SettingsDialogViewModel(true, true, true, true)
        {
            GoldLimitText = "125",
            DailyGoldSpendingLimitText = "25",
            SilverLimitText = "250",
            DailySilverSpendingLimitText = "invalid",
            DailyGoldSpent = 3,
            DailySilverSpent = -4,
        };

        Assert.Equal("125", vm.GoldLimitText);
        Assert.Equal("250", vm.SilverLimitText);
        Assert.Equal("3 / 25", vm.DailyGoldSpendingUsageText);
        Assert.Equal("0 / ?", vm.DailySilverSpendingUsageText);
    }
}
