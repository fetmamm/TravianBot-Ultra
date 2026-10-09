using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageTaskPriorityTests
{
    [Fact]
    public void MissingOrder_UsesAgreedDefault()
    {
        Assert.Equal(
        [
            "hero", "construction", "demolish", "troops", "brewery_celebration",
            "town_hall_celebration", "farming", "troop_training", "resource_transfer",
            "npc_trade", "reinforcements",
        ], VillageTaskPriorityOrder.Resolve(null));
    }

    [Fact]
    public void UntouchedLegacyDashboardOrder_UsesNewDefault()
    {
        Assert.Equal(VillageTaskPriorityOrder.DefaultKeys, VillageTaskPriorityOrder.Resolve(
        [
            "brewery_celebration", "town_hall_celebration", "hero", "construction",
            "troops", "farming", "troop_training", "resource_transfer",
            "reinforcements", "demolish",
        ]));
    }

    [Fact]
    public void CustomizedLegacyDashboardOrder_IsPreservedAndCompleted()
    {
        var result = VillageTaskPriorityOrder.Resolve(
        [
            "construction", "hero", "farming", "troops", "brewery_celebration",
            "town_hall_celebration", "troop_training", "resource_transfer",
            "reinforcements", "demolish",
        ]);

        Assert.Equal("construction", result[0]);
        Assert.Equal("hero", result[1]);
        Assert.Equal("farming", result[2]);
        Assert.True(result.ToList().IndexOf("npc_trade") < result.ToList().IndexOf("reinforcements"));
        Assert.Equal(11, result.Count);
    }

    [Fact]
    public void MoveAndReset_KeepDisabledGroupsAndNotifyOnlyForRealChanges()
    {
        var vm = new VillageTaskPriorityViewModel();
        var changes = 0;
        vm.Changed += () => changes++;
        vm.Load(null, ["construction"]);

        Assert.False(vm.Rows[0].IsAutomationEnabled);
        Assert.True(vm.Rows[1].IsAutomationEnabled);
        vm.MoveUpCommand.Execute(vm.Rows[0]);
        Assert.Equal(0, changes);
        vm.MoveUpCommand.Execute(vm.Rows[1]);
        Assert.Equal("construction", vm.Rows[0].Key);
        Assert.Equal(1, changes);

        vm.ResetToDefault();
        Assert.Equal(VillageTaskPriorityOrder.DefaultKeys, vm.OrderKeys);
        Assert.True(vm.Rows[1].IsAutomationEnabled);
        Assert.Equal(2, changes);
        vm.ResetToDefault();
        Assert.Equal(2, changes);
    }
}
