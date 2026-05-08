using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Common;

/// <summary>
/// Base class for view models. Provides INotifyPropertyChanged with a
/// SetProperty helper that raises PropertyChanged only when the value
/// actually changes.
/// </summary>
public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Updates a backing field and raises PropertyChanged when the new value
    /// differs from the current one. Returns true if the value changed.
    /// </summary>
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
