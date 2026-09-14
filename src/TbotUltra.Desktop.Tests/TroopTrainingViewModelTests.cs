using System.Collections.Generic;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TroopTrainingViewModelTests
{
    [Fact]
    public void ResetRuntimeState_ClearsPreviousAccountQueuesAndBrewery()
    {
        var vm = new TroopTrainingViewModel();
        vm.Initialize();
        vm.Buildings[0].Exists = true;
        vm.Buildings[0].QueueRemainingSeconds = 120;
        vm.MarkBreweryExists(true);
        vm.PushBreweryCelebrationRemainingSeconds(300, "Running.");
        vm.InfoText = "Loaded previous account.";

        vm.ResetRuntimeState();

        Assert.Empty(vm.InfoText);
        Assert.False(vm.BreweryExists);
        Assert.Null(vm.AutoCelebrationRemainingSeconds);
        Assert.All(vm.Buildings, item =>
        {
            Assert.False(item.Exists);
            Assert.Null(item.QueueRemainingSeconds);
            Assert.Equal("Queue not loaded.", item.QueueStatusText);
        });
    }

    [Fact]
    public void ApplyStatus_WithoutQueueStatus_ClearsPreviousVillageTimer()
    {
        var vm = new TroopTrainingViewModel();
        vm.Initialize();
        vm.Buildings[0].Exists = true;
        vm.Buildings[0].QueueRemainingSeconds = 36000;
        vm.Buildings[0].QueueStatusText = "Queue: 10:00:00";

        vm.ApplyStatus(
            new VillageStatus(
                ActiveVillage: "Village Two",
                Villages: [],
                Resources: new Dictionary<string, string>(),
                ResourceFields: [],
                Buildings: [new Building(19, "Barracks", 1, "build.php?id=19", 19)],
                BuildQueue: []),
            fallbackQueues: null);

        Assert.True(vm.Buildings[0].Exists);
        Assert.Null(vm.Buildings[0].QueueRemainingSeconds);
        Assert.Null(vm.Buildings[0].QueueFinish);
        Assert.Equal("Queue not loaded.", vm.Buildings[0].QueueStatusText);
        Assert.Equal("00:00h", vm.Buildings[0].QueueTimerText);
    }

    [Fact]
    public void SyncBreweryCelebrationLoopWait_ShowsLoopTimerInsteadOfReady()
    {
        var vm = ReadyCelebrationViewModel();
        Assert.Equal("Ready", vm.AutoCelebrationTimerText);

        vm.SyncBreweryCelebrationLoopWait(53237); // 14h 47m 17s — the dashboard loop row countdown

        Assert.Equal("14:47:17", vm.AutoCelebrationTimerText);
    }

    [Fact]
    public void RunningCelebrationTimer_TakesPrecedenceOverLoopWait()
    {
        var vm = ReadyCelebrationViewModel();
        vm.SyncBreweryCelebrationLoopWait(53237);

        // A live/running celebration timer is the more authoritative reading and wins.
        vm.PushBreweryCelebrationRemainingSeconds(65, "Celebration running.");

        Assert.Equal("01:05", vm.AutoCelebrationTimerText);
    }

    [Fact]
    public void ResetRuntimeState_ClearsMirroredLoopTimer()
    {
        var vm = ReadyCelebrationViewModel();
        vm.SyncBreweryCelebrationLoopWait(53237);

        vm.ResetRuntimeState();

        Assert.Equal("N/A", vm.AutoCelebrationTimerText);
    }

    [Fact]
    public void ManualStatusCommands_ShareBusyGateWithoutBlockingBuildNow()
    {
        var vm = new TroopTrainingViewModel();
        var buildRequested = false;
        vm.BuildNowRequested += () => buildRequested = true;

        vm.SetManualRefreshRunning(true);
        vm.BuildNowCommand.Execute(null);

        Assert.False(vm.RefreshQueuesCommand.CanExecute(null));
        Assert.False(vm.CheckCelebrationCommand.CanExecute(null));
        Assert.True(buildRequested);

        vm.SetManualRefreshRunning(false);

        Assert.True(vm.RefreshQueuesCommand.CanExecute(null));
        Assert.True(vm.CheckCelebrationCommand.CanExecute(null));
    }

    [Fact]
    public void TryValidateMinimumTroopRanges_RejectsMaxBelowMinWhenEnabled()
    {
        var vm = new TroopTrainingViewModel();
        vm.Initialize();
        vm.Buildings[0].MinimumTroopsEnabled = true;
        vm.Buildings[0].MinimumTroops = 100;
        vm.Buildings[0].MaximumMinimumTroops = 20;

        Assert.False(vm.TryValidateMinimumTroopRanges(out var error));
        Assert.Contains("Max must be at least Min", error);

        vm.Buildings[0].MinimumTroopsEnabled = false;
        Assert.True(vm.TryValidateMinimumTroopRanges(out _));
    }

    [Fact]
    public void TryValidateMinimumTroopRanges_RequiresAResourceForPercentMode()
    {
        var vm = new TroopTrainingViewModel();
        vm.Initialize();
        vm.Buildings[0].IsEnabled = true;
        vm.Buildings[0].RunMode = "resource_percent";
        vm.CheckWood = false;
        vm.CheckClay = false;
        vm.CheckIron = false;
        vm.CheckCrop = false;

        Assert.False(vm.TryValidateMinimumTroopRanges(out var error));
        Assert.Contains("Select at least one resource", error);

        vm.CheckClay = true;
        Assert.True(vm.TryValidateMinimumTroopRanges(out _));
    }

    [Fact]
    public void AutomaticResourceSelection_DisablesManualChecksAndSatisfiesValidation()
    {
        var vm = new TroopTrainingViewModel();
        vm.Initialize();
        vm.Buildings[0].IsEnabled = true;
        vm.Buildings[0].RunMode = "resource_percent";
        vm.CheckWood = false;
        vm.CheckClay = false;
        vm.CheckIron = false;
        vm.CheckCrop = false;

        vm.AutomaticResourceSelection = true;

        Assert.False(vm.IsManualResourceSelectionEnabled);
        Assert.True(vm.TryValidateMinimumTroopRanges(out _));
        Assert.All(
            new[]
            {
                vm.BuildVillageTrainingPayload().Barracks,
                vm.BuildVillageTrainingPayload().Stable,
                vm.BuildVillageTrainingPayload().Workshop,
            },
            building => Assert.True(building.AutomaticResourceSelection));
    }

    private static TroopTrainingViewModel ReadyCelebrationViewModel()
    {
        var vm = new TroopTrainingViewModel();
        vm.Initialize();
        vm.UpdateAutoCelebrationAvailability("Teutons");
        vm.ApplyBreweryCelebrationStatus(new BreweryCelebrationStatus(
            IsAvailableForTribe: true,
            IsCapital: true,
            BreweryExists: true,
            BrewerySlotId: 35,
            CelebrationRunning: false,
            RemainingSeconds: null,
            RemainingText: string.Empty,
            StatusText: "Ready."));
        return vm;
    }
}
