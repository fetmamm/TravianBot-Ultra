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

        Assert.Equal("Configure troop building rules and refresh queues when needed.", vm.InfoText);
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
}
