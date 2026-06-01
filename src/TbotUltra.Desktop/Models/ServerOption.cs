using System.ComponentModel;
using System.Text.Json.Serialization;
using TbotUltra.Core.Configuration;

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
            // The detected server flavor depends on the URL, so refresh it too.
            OnPropertyChanged(nameof(ServerFlavor));
            OnPropertyChanged(nameof(ServerTypeLabel));
        }
    }

    /// <summary>
    /// The server flavor detected from <see cref="BaseUrl"/>. Not persisted – it is always
    /// derived from the URL so the catalog stays a simple name/url list.
    /// </summary>
    [JsonIgnore]
    public ServerFlavor ServerFlavor => ServerFlavorDetector.FromBaseUrl(BaseUrl);

    /// <summary>
    /// Human-readable server type shown in the UI ("Official" / "SS-Travi").
    /// </summary>
    [JsonIgnore]
    public string ServerTypeLabel => ServerFlavor == ServerFlavor.SsTravi ? "SS-Travi" : "Official";

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
