using System.Windows;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private MapOasisWindow? _mapOasisWindow;

    private void AnalyzeMapOasisButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mapOasisWindow is { IsVisible: true })
        {
            _mapOasisWindow.Activate();
            return;
        }

        var options = LoadBotOptions();
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            AppendLog("[map-oasis-ui] No active Travian server URL is available.");
            return;
        }

        var analyzer = new MapAnalyzerService(_projectRoot, AppendLog);
        var window = new MapOasisWindow(_travcoListStore, AppendLog)
        {
            Owner = this,
            AnalyzeRequested = (includeOccupied, selectedTypes) =>
                analyzer.AnalyzeAsync(options.BaseUrl, includeOccupied, selectedTypes),
        };
        window.Closed += MapOasisWindow_Closed;
        _mapOasisWindow = window;
        window.Show();
    }

    private void MapOasisWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is MapOasisWindow window)
        {
            window.Closed -= MapOasisWindow_Closed;
        }

        _mapOasisWindow = null;
    }
}
