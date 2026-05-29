using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class FarmListSelectionOption : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Name { get; init; } = string.Empty;
    public int ActiveFarmCount { get; init; }
    public int TotalFarmCount { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public int AvailableSlots => Math.Max(0, TotalFarmCount - ActiveFarmCount);

    public bool IsFull => AvailableSlots <= 0;

    public string CountText => $"{ActiveFarmCount}/{TotalFarmCount}";

    public string CapacityText => $"{AvailableSlots} slots left";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
