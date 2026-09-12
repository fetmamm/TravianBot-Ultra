using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListsViewModelTests
{
    private static FarmListStatusRow Real(string name, int? remainingSeconds = null) =>
        new()
        {
            Name = name,
            TotalFarmCount = 1,
            RemainingSeconds = remainingSeconds,
            IntervalMinMinutesText = "15",
            IntervalMaxMinutesText = "30",
        };

    [Fact]
    public void EnsurePlaceholderRow_EmptyCollectionGetsOnePlaceholder()
    {
        var vm = new FarmListsViewModel();

        vm.EnsurePlaceholderRow();
        vm.EnsurePlaceholderRow();

        var row = Assert.Single(vm.FarmLists);
        Assert.True(row.IsPlaceholder);
        Assert.False(row.IsEnabled);
    }

    [Fact]
    public void EnsurePlaceholderRow_RealRowsRemovePlaceholder()
    {
        var vm = new FarmListsViewModel();
        vm.EnsurePlaceholderRow();
        vm.FarmLists.Add(Real("List A"));

        vm.EnsurePlaceholderRow();

        Assert.DoesNotContain(vm.FarmLists, row => row.IsPlaceholder);
        Assert.Single(vm.FarmLists);
    }

    [Fact]
    public void DescribeStatus_NoRealListsPromptsAnalyze()
    {
        var vm = new FarmListsViewModel();
        vm.EnsurePlaceholderRow();

        Assert.Equal("No farm lists loaded. Click Analyze Farmlists.", vm.DescribeStatus());
    }

    [Fact]
    public void DescribeStatus_CountsLoadedAndReadyLists()
    {
        var vm = new FarmListsViewModel();
        vm.FarmLists.Add(Real("List A"));
        vm.FarmLists.Add(Real("List B", remainingSeconds: 120));

        Assert.Equal("Loaded 2 farm list(s). Ready: 1.", vm.DescribeStatus());
    }

    [Fact]
    public void EmptyFarmList_IsNotReadyToSend_AndShowsEmptyAction()
    {
        var row = new FarmListStatusRow
        {
            Name = "Empty list",
            IsEnabled = true,
            TotalFarmCount = 0,
        };

        Assert.True(row.IsEmpty);
        Assert.Equal("Empty", row.ReadyText);
        Assert.Equal("Empty", row.ActionText);
        Assert.False(row.CanSendNow);
    }

    [Fact]
    public void LastSentText_UsesElapsedTimeAndKeepsDisabledListsNeutral()
    {
        var row = new FarmListStatusRow
        {
            Name = "Raiders",
            IsEnabled = true,
            LastSentAtUtc = DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-2),
        };

        Assert.Matches(@"^01:02:\d{2}$", row.LastSentText);

        row.IsEnabled = false;

        Assert.Equal("00:00:00", row.LastSentText);
    }

    [Fact]
    public void LastSentText_AppliesConfiguredLimitOrHardFiveDayCap()
    {
        var row = new FarmListStatusRow
        {
            Name = "Raiders",
            IsEnabled = true,
            LastSentAtUtc = DateTimeOffset.UtcNow.AddHours(-25),
            LastSentLimitEnabled = true,
            LastSentLimitHours = 24,
        };

        Assert.Equal("24h+", row.LastSentText);

        row.LastSentLimitEnabled = false;

        Assert.Matches(@"^25:00:\d{2}$", row.LastSentText);
        row.LastSentAtUtc = DateTimeOffset.UtcNow.AddHours(-121);
        Assert.Equal("120h+", row.LastSentText);
    }

    [Fact]
    public void DispatchInterval_IsRequiredAndRequiresOrderedPositiveValues()
    {
        var row = new FarmListStatusRow { Name = "Raiders", TotalFarmCount = 1 };

        Assert.False(row.TryGetDispatchInterval(out _, out _));
        Assert.True(row.HasIntervalError);
        Assert.Equal("Min and Max are required.", row.IntervalErrorText);

        row.IntervalMinMinutesText = "10";
        row.IntervalMaxMinutesText = "5";
        Assert.False(row.TryGetDispatchInterval(out _, out _));
        Assert.True(row.HasIntervalError);

        row.IntervalMaxMinutesText = "20";
        Assert.True(row.TryGetDispatchInterval(out var min, out var max));
        Assert.Equal(10, min);
        Assert.Equal(20, max);
    }

    [Fact]
    public void IsRealRow_PlaceholderIsNotReal()
    {
        Assert.False(FarmListsViewModel.IsRealRow(new FarmListStatusRow { IsPlaceholder = true }));
        Assert.True(FarmListsViewModel.IsRealRow(Real("List A")));
    }

    [Fact]
    public void Commands_FollowGlobalAvailabilityAndForwardSelectedFarmList()
    {
        var vm = new FarmListsViewModel();
        var row = Real("List A");
        FarmListStatusRow? requested = null;
        vm.SendNowRequested += value => requested = value;

        vm.UpdateCommandAvailability(canAnalyze: false, canManageLists: false, canCreate: false, canSendAll: false);

        Assert.False(vm.AnalyzeCommand.CanExecute(null));
        Assert.False(vm.SendNowCommand.CanExecute(row));

        vm.UpdateCommandAvailability(canAnalyze: true, canManageLists: true, canCreate: true, canSendAll: true);
        vm.SendNowCommand.Execute(row);

        Assert.True(vm.AnalyzeCommand.CanExecute(null));
        Assert.Same(row, requested);
    }

    [Fact]
    public void BuildFarmListVillageHeader_UsesKnownCoordinatesImmediately()
    {
        var header = MainWindow.BuildFarmListVillageHeader(
            "Swollster",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Swollster"] = "(120 | 14)",
            });

        Assert.Equal("Swollster (120 | 14)", header);
    }

    [Fact]
    public void FarmingSettings_NotifyForUserChanges_NotInitialLoad()
    {
        var vm = new FarmListsViewModel();
        var changes = 0;
        vm.SettingsChanged += () => changes++;

        vm.LoadSettings(
            sendMode: FarmingDefaults.SendModeAllAtOnce,
            dispatchDelayMinMinutes: 10,
            dispatchDelayMaxMinutes: 20,
            deactivateRedLosses: true,
            deactivateYellowLosses: true,
            deactivateRedOasisLosses: false,
            deactivateYellowOasisLosses: false,
            moveRedLosses: false,
            moveYellowLosses: false);
        vm.DeactivateOasisLosses = true;

        Assert.Equal(1, changes);
        Assert.True(vm.SendAllLists);
        Assert.Equal("10", vm.DispatchDelayMinMinutes);
    }

    [Fact]
    public void DispatchMode_SeparatesIndividualSharedAndSendAllBehavior()
    {
        var vm = new FarmListsViewModel();

        Assert.Equal("Default interval", vm.DispatchIntervalTitle);
        Assert.Contains("first loaded", vm.DispatchIntervalDescription);
        Assert.True(vm.UseIndividualSchedules);

        vm.UseSharedSchedule = true;

        Assert.Equal("Shared interval", vm.DispatchIntervalTitle);
        Assert.Contains("enabled lists", vm.DispatchModeDescription);
        Assert.True(vm.UseSharedSchedule);

        vm.SendAllLists = true;

        Assert.Contains("Start all", vm.DispatchModeDescription);
        Assert.True(vm.SendAllLists);
        Assert.False(vm.UseSharedSchedule);
    }

    [Fact]
    public void MoveLosses_UserEnableRequestsMatchingDestinationSetup_InitialLoadDoesNot()
    {
        var vm = new FarmListsViewModel();
        var requests = 0;
        vm.MoveRedLossesEnabledRequested += () => requests++;

        vm.LoadSettings(
            sendMode: FarmingDefaults.SendModeListPerList,
            dispatchDelayMinMinutes: 15,
            dispatchDelayMaxMinutes: 30,
            deactivateRedLosses: true,
            deactivateYellowLosses: true,
            deactivateRedOasisLosses: false,
            deactivateYellowOasisLosses: false,
            moveRedLosses: true,
            moveYellowLosses: false);

        Assert.Equal(0, requests);

        vm.MoveRedLosses = false;
        vm.MoveRedLosses = true;

        Assert.Equal(1, requests);
    }

    [Fact]
    public void ReplaceLossDestinations_KeepsCollectionBindingStable_WithoutSavingSettings()
    {
        var vm = new FarmListsViewModel();
        var collection = vm.LossDestinations;
        var changes = 0;
        vm.SettingsChanged += () => changes++;
        var selected = new FarmLossDestinationOption("42", "yellow", "Capital", 3, 100);

        vm.ReplaceLossDestinations(
            [
                new FarmLossDestinationOption("41", "raiders", "Capital", 10, 100),
                selected,
            ],
            selected,
            null);

        Assert.Same(collection, vm.LossDestinations);
        Assert.Equal(2, vm.LossDestinations.Count);
        Assert.Same(selected, vm.SelectedRedLossDestination);
        Assert.Null(vm.SelectedYellowLossDestination);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void OasisColors_SynchronizeMasterSelection()
    {
        var vm = new FarmListsViewModel();

        vm.DeactivateOasisLosses = true;
        Assert.True(vm.DeactivateRedOasisLosses);
        Assert.True(vm.DeactivateYellowOasisLosses);

        vm.DeactivateRedOasisLosses = false;
        Assert.True(vm.DeactivateOasisLosses);
        vm.DeactivateYellowOasisLosses = false;
        Assert.False(vm.DeactivateOasisLosses);

        vm.DeactivateRedOasisLosses = true;
        Assert.True(vm.DeactivateOasisLosses);
    }

    [Fact]
    public void DisablingLossColor_DisablesOnlyMatchingMoveAndKeepsDestination()
    {
        var vm = new FarmListsViewModel();
        var destination = new FarmLossDestinationOption("42", "Red farms", "Capital", 3, 100);
        vm.ReplaceLossDestinations([destination], destination, null);
        vm.DeactivateRedLosses = true;
        vm.MoveRedLosses = true;

        vm.DeactivateRedLosses = false;

        Assert.False(vm.MoveRedLosses);
        Assert.Same(destination, vm.SelectedRedLossDestination);
    }
}
