using System.ComponentModel;
using System.Runtime.CompilerServices;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.Models;

// Row for the Village settings window. Village/Pop/Coords are read-only display values. The "Auto"
// toggle (IsEnabledForAutomation) is wired to VillageSettingsStore; the remaining flags are per-village
// toggles that will be wired to config later. INotifyPropertyChanged so the window can persist the Auto
// toggle the moment the user flips it.
public sealed class VillageSettingsRow : INotifyPropertyChanged
{
    public string Name { get; init; } = string.Empty;
    public string PopText { get; init; } = string.Empty;
    public string CoordsText { get; init; } = string.Empty;

    // Stable village identity used to persist the enabled choice. Not displayed.
    public VillageSettingsStore.VillageKeyInfo? KeyInfo { get; init; }

    private bool _isEnabledForAutomation;
    public bool IsEnabledForAutomation
    {
        get => _isEnabledForAutomation;
        set
        {
            if (_isEnabledForAutomation == value)
            {
                return;
            }

            _isEnabledForAutomation = value;
            OnPropertyChanged();
        }
    }

    public bool HeroResources { get; set; }
    public bool NpcTrade { get; set; }
    public bool BuildTroops { get; set; }
    public bool UpgradeTroops { get; set; }
    public bool Farming { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
