using System.ComponentModel;
using System.Runtime.CompilerServices;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class TravcoListRow : INotifyPropertyChanged
{
    private bool _selected = true;

    public double? Distance { get; init; }
    public string DistanceText => Distance?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
    public string Account { get; init; } = string.Empty;
    public string Village { get; init; } = string.Empty;
    public long? Pop { get; init; }
    public string PopText => Pop?.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
    public string Coordinates { get; init; } = string.Empty;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
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
}
