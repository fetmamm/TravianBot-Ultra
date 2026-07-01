using System.ComponentModel;

namespace TbotUltra.Desktop.Models;

public sealed class ServerOption : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (_baseUrl == value)
            {
                return;
            }

            _baseUrl = value;
            OnPropertyChanged(nameof(BaseUrl));
        }
    }

    public override string ToString()
    {
        return Name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
