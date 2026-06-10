using System.ComponentModel;
using System.Runtime.CompilerServices;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class TravcoListRow : INotifyPropertyChanged
{
    private bool _selected = true;
    private double? _distance;
    private string _account = string.Empty;
    private string _village = string.Empty;
    private long? _pop;
    private string _coordinates = string.Empty;

    // Oasis metadata, only populated for map-oasis lists. Carried through the window so opening,
    // toggling or editing an oasis list never strips the data the farm-add filter depends on.
    public string? OasisType { get; set; }
    public bool? IsOccupied { get; set; }
    public string? Animals { get; set; }
    public string? OwnerPlayer { get; set; }
    public string? OwnerAlliance { get; set; }

    public double? Distance
    {
        get => _distance;
        set
        {
            if (_distance == value)
            {
                return;
            }

            _distance = value;
            OnPropertyChanged(nameof(Distance));
            OnPropertyChanged(nameof(DistanceText));
        }
    }

    public string DistanceText => Distance?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    public string Account
    {
        get => _account;
        set
        {
            if (_account == value)
            {
                return;
            }

            _account = value;
            OnPropertyChanged(nameof(Account));
        }
    }

    public string Village
    {
        get => _village;
        set
        {
            if (_village == value)
            {
                return;
            }

            _village = value;
            OnPropertyChanged(nameof(Village));
        }
    }

    public long? Pop
    {
        get => _pop;
        set
        {
            if (_pop == value)
            {
                return;
            }

            _pop = value;
            OnPropertyChanged(nameof(Pop));
            OnPropertyChanged(nameof(PopText));
        }
    }

    public string PopText => Pop?.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    public string Coordinates
    {
        get => _coordinates;
        set
        {
            if (_coordinates == value)
            {
                return;
            }

            _coordinates = value;
            OnPropertyChanged(nameof(Coordinates));
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            OnPropertyChanged(nameof(Selected));
        }
    }

    public static TravcoListRow FromWorker(TravcoRow row)
    {
        return new TravcoListRow
        {
            Distance = row.Distance,
            Account = row.Account,
            Village = row.Village,
            Pop = row.Pop,
            Coordinates = row.Coordinates,
            Selected = true,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
