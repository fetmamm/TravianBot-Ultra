using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.Models;

// Row for the Village settings window. Village/Pop are read-only display values. The "Auto" toggle
// (IsEnabledForAutomation) and "NPC" toggle are wired to VillageSettingsStore. GroupToggles mirrors the
// dashboard automation-loop cards per village (enabled-group set), so the user can turn groups on/off for
// many villages at once. INotifyPropertyChanged so the window can persist each change immediately.
public sealed class VillageSettingsRow : INotifyPropertyChanged
{
    public string Name { get; init; } = string.Empty;
    public string PopText { get; init; } = string.Empty;

    // Stable village identity used to persist the enabled choice. Not displayed.
    public VillageSettingsStore.VillageKeyInfo? KeyInfo { get; init; }

    // Per-village automation-group toggles (one per visible dashboard card). The window subscribes to each
    // toggle's PropertyChanged to persist the village's enabled-group set.
    public IReadOnlyList<VillageGroupToggle> GroupToggles { get; init; } = [];

    private bool _heroResourcesEnabled;
    public bool HeroResourcesEnabled
    {
        get => _heroResourcesEnabled;
        set
        {
            if (_heroResourcesEnabled == value)
            {
                return;
            }

            _heroResourcesEnabled = value;
            OnPropertyChanged();
        }
    }

    private bool _constructFasterEnabled;
    public bool ConstructFasterEnabled
    {
        get => _constructFasterEnabled;
        set
        {
            if (_constructFasterEnabled == value)
            {
                return;
            }

            _constructFasterEnabled = value;
            OnPropertyChanged();
        }
    }

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

    // NPC trade per village. Notifies so the window can persist the choice the moment it changes.
    private bool _npcTrade;
    public bool NpcTrade
    {
        get => _npcTrade;
        set
        {
            if (_npcTrade == value)
            {
                return;
            }

            _npcTrade = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
