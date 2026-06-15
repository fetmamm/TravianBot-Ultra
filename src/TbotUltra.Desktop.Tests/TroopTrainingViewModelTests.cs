using TbotUltra.Desktop.ViewModels;
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
}
