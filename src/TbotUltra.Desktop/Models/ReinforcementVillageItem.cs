using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class ReinforcementVillageItem : INotifyPropertyChanged
{
    private bool _isSource;
    private bool _isTarget;
    private string _troopSummaryText = string.Empty;

    public string Name { get; init; } = string.Empty;
    public int? CoordX { get; init; }
    public int? CoordY { get; init; }

    public bool IsSource
    {
        get => _isSource;
        set
        {
            if (_isTarget && value)
            {
                value = false;
            }

            if (_isSource == value)
            {
                return;
            }

            _isSource = value;
            OnPropertyChanged();
        }
    }

    public bool IsTarget
    {
        get => _isTarget;
        set
        {
            if (_isTarget == value)
            {
                return;
            }

            _isTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectAsSource));
            OnPropertyChanged(nameof(SourceStatusText));

            if (_isTarget)
            {
                IsSource = false;
            }
        }
    }

    public bool CanSelectAsSource => !IsTarget;
    public string SourceStatusText => IsTarget ? "Target" : string.Empty;

    public string TroopSummaryText
    {
        get => _troopSummaryText;
        set
        {
            if (_troopSummaryText == value)
            {
                return;
            }

            _troopSummaryText = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName => CoordX.HasValue && CoordY.HasValue
        ? $"{Name} ({CoordX} | {CoordY})"
        : Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
