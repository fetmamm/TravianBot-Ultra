using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.ViewModels;
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
}
