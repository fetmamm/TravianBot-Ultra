using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class HeroViewModelTests
{
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
