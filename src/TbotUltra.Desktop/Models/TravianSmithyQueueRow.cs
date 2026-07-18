using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class TravianSmithyQueueRow : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _levelText = "-";
    private string _countdownText = "-";
    private string _finishAtText = "-";

    public string Name { get => _name; init => _name = value; }
    public string LevelText { get => _levelText; init => _levelText = value; }
    public string CountdownText { get => _countdownText; init => _countdownText = value; }
    public string FinishAtText { get => _finishAtText; init => _finishAtText = value; }

    public void ApplySnapshot(TravianSmithyQueueRow snapshot)
    {
        Set(ref _name, snapshot.Name, nameof(Name));
        Set(ref _levelText, snapshot.LevelText, nameof(LevelText));
        Set(ref _countdownText, snapshot.CountdownText, nameof(CountdownText));
        Set(ref _finishAtText, snapshot.FinishAtText, nameof(FinishAtText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, System.StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
