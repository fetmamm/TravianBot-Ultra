using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class MapOasisSettingsTests
{
    [Fact]
    public void BuildConfirmationMessage_WarnsForWholeMap()
    {
        var request = new MapOasisScanRequest(0, 0, MapOasisScanScope.WholeMap, 40, MapOasisScanSpeed.Normal);
        var estimate = MapOasisScanEstimate.Calculate(request);

        var message = MapOasisSettingsWindow.BuildConfirmationMessage(request, estimate, hasPreviousScan: false);

        Assert.Contains("whole-map", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("169 requests", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfirmationMessage_WarnsForRepeatedRadiusScanAndExplainsAnimals()
    {
        var request = new MapOasisScanRequest(0, 0, MapOasisScanScope.Radius, 40, MapOasisScanSpeed.Normal);
        var estimate = MapOasisScanEstimate.Calculate(request);

        var message = MapOasisSettingsWindow.BuildConfirmationMessage(request, estimate, hasPreviousScan: true);

        Assert.Contains("previous scan", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("animals", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildConfirmationMessage_SkipsWarningForFirstRadiusScan()
    {
        var request = new MapOasisScanRequest(0, 0, MapOasisScanScope.Radius, 40, MapOasisScanSpeed.Normal);

        var message = MapOasisSettingsWindow.BuildConfirmationMessage(
            request,
            MapOasisScanEstimate.Calculate(request),
            hasPreviousScan: false);

        Assert.Null(message);
    }
}
