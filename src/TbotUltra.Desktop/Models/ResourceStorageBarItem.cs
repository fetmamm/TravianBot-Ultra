using System.Windows.Media;
using TbotUltra.Desktop.Common;

namespace TbotUltra.Desktop.Models;

public sealed class ResourceStorageBarItem : BaseViewModel
{
    private double _percentValue;
    private string _percentText = "-";
    private string _currentMaxText = "-/-";
    private string _productionText = "-/h";
    private string _timeUntilFullText = "-";
    private string _tooltipText = "No data loaded.";
    private bool _isFull;
    private Brush _statusBrush = Brushes.Black;

    public string ResourceKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Brush BarBrush { get; init; } = Brushes.Gray;
    public Brush TrackBrush { get; init; } = Brushes.LightGray;

    public double PercentValue
    {
        get => _percentValue;
        set => SetProperty(ref _percentValue, value);
    }

    public string PercentText
    {
        get => _percentText;
        set => SetProperty(ref _percentText, value);
    }

    public string CurrentMaxText
    {
        get => _currentMaxText;
        set => SetProperty(ref _currentMaxText, value);
    }

    public string ProductionText
    {
        get => _productionText;
        set => SetProperty(ref _productionText, value);
    }

    public string TimeUntilFullText
    {
        get => _timeUntilFullText;
        set => SetProperty(ref _timeUntilFullText, value);
    }

    public string TooltipText
    {
        get => _tooltipText;
        set => SetProperty(ref _tooltipText, value);
    }

    public bool IsFull
    {
        get => _isFull;
        set => SetProperty(ref _isFull, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set => SetProperty(ref _statusBrush, value);
    }
}
