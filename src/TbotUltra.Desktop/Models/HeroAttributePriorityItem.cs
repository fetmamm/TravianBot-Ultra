using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class HeroAttributePriorityItem : INotifyPropertyChanged
{
    private int _order;
    private string _pointsText = "-";

    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    public int Order
    {
        get => _order;
        set
        {
            if (_order == value)
            {
                return;
            }

            _order = value;
            OnPropertyChanged();
        }
    }

    public string PointsText
    {
        get => _pointsText;
        set
        {
            if (string.Equals(_pointsText, value, StringComparison.Ordinal))
            {
                return;
            }

            _pointsText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
