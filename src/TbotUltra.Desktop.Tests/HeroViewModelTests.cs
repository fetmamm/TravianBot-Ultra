using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class HeroViewModelTests
{
    [Fact]
    public void LoadSettingsFromConfig_UsesResourcesFirstDefaultPriority()
    {
        var vm = new HeroViewModel();

        vm.LoadSettingsFromConfig(new BotOptions());

        Assert.Equal(
            ["resources", "fighting_strength", "offence_bonus", "defence_bonus"],
            vm.AttributePriorityItems.Select(item => item.Key));
        Assert.Equal(
            "resources,fighting_strength,offence_bonus,defence_bonus",
            vm.BuildPriorityPayload());
    }

    [Fact]
    public void BuildPriorityPayload_PreservesUiOrder()
    {
        var vm = new HeroViewModel();
        vm.LoadPriorityFromConfig("resources,fighting_strength,offence_bonus,defence_bonus");

        vm.AttributePriorityItems.Move(1, 0);
        vm.UpdateOrders();

        Assert.Equal(
            "fighting_strength,resources,offence_bonus,defence_bonus",
            vm.BuildPriorityPayload());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadSettingsFromConfig_LoadsAutoUseOintments(bool enabled)
    {
        var vm = new HeroViewModel();
        var options = new BotOptions
        {
            HeroAutoUseOintments = enabled,
        };

        vm.LoadSettingsFromConfig(options);

        Assert.Equal(enabled, vm.AutoUseOintments);
    }

    [Fact]
    public void ResetRuntimeState_ClearsPreviousAccountValues()
    {
        var vm = new HeroViewModel();
        vm.LoadPriorityFromConfig(null);
        vm.ApplyAttributeSnapshot(new HeroAttributeSnapshot(
            FreePoints: 5,
            FightingStrength: 10,
            OffenceBonus: 20,
            DefenceBonus: 30,
            Resources: 40));
        vm.ApplyInventory(new HeroInventoryResources(1, 2, 3, 4));
        vm.AdventureCountText = "7";

        vm.ResetRuntimeState();

        Assert.Equal("?", vm.AdventureCountText);
        Assert.Equal("-", vm.HeroInventoryWood);
        Assert.Equal("Hero stats not loaded.", vm.AttributesStatusText);
        Assert.All(vm.AttributePriorityItems, item => Assert.Equal("-", item.PointsText));
    }
}
