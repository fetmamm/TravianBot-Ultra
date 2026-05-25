using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class ResourceTransferVillageItem : INotifyPropertyChanged
{
    private bool _isSource;
    private bool _isTarget;

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

    public string DisplayName
    {
        get
        {
            if (CoordX.HasValue && CoordY.HasValue)
            {
                return $"{Name} ({CoordX} | {CoordY})";
            }

            return Name;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
