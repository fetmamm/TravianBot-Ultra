using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

// One per-village automation-group toggle shown in the Village settings popup. Mirrors a dashboard
// automation-loop "card" (GroupKey == the card's group key). Two-way bound; the popup persists the
// village's enabled-group set to VillageSettingsStore whenever IsEnabled flips.
public sealed class VillageGroupToggle : INotifyPropertyChanged
{
    public string GroupKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    // Short description of what the group does, shown in the column-header tooltip.
    public string Description { get; init; } = string.Empty;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
