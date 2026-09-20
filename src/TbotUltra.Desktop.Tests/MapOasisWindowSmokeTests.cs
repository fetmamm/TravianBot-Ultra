using System.Windows.Controls;
using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

[Collection(WpfSmokeCollection.Name)]
public sealed class MapOasisWindowSmokeTests
{
    private readonly WpfSmokeFixture _wpf;

    public MapOasisWindowSmokeTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void DefaultsToRadiusFortyAndShowsRequestEstimate()
    {
        _wpf.Run(() =>
        {
            var village = new VillageSelectionItem { Name = "WhyNot", CoordX = 101, CoordY = 114 };
            var window = new MapOasisSettingsWindow([village], village);
            try
            {
                Assert.True(Assert.IsType<RadioButton>(window.FindName("RadiusRadioButton")).IsChecked);
                Assert.False(Assert.IsType<RadioButton>(window.FindName("WholeMapRadioButton")).IsChecked);
                Assert.Equal("40", Assert.IsType<TextBox>(window.FindName("RadiusTextBox")).Text);
                var estimate = Assert.IsType<TextBlock>(window.FindName("RequestEstimateTextBlock")).Text;
                Assert.Contains("zoom 3: 9", estimate, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("zoom 2: 20", estimate, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("zoom 1: 72", estimate, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
