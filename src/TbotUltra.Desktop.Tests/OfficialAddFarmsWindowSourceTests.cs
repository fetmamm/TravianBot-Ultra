using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class OfficialAddFarmsWindowSourceTests
{
    [Fact]
    public void OasisFilter_TrustsRawScanOccupancyByDefault()
    {
        var xaml = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Desktop",
            "OfficialAddFarmsWindow.xaml"));

        Assert.Contains("x:Name=\"TrustSavedOasisOccupancyCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Trust raw scan occupancy (faster)\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"True\"", xaml, StringComparison.Ordinal);
    }
}
