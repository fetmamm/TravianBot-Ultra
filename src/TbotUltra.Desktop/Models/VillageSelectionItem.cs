using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public sealed class VillageSelectionItem : INotifyPropertyChanged
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsCapital { get; init; }
    public int? CoordX { get; init; }
    public int? CoordY { get; init; }
    public int? Population { get; init; }
    public int? CropFields { get; init; }

    // Whether this village is enabled for automation. Two-way bound to the Dashboard toggle; the
    // toggle's Checked/Unchecked handler persists changes to VillageSettingsStore. Settable (unlike
    // the identity fields) so the bound toggle reflects and updates the stored choice.
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

    // Whether this is the village the program is currently working in (the one open in the browser).
    // Distinct from being the selected/viewed village. Drives the colored "active" border on the
    // Dashboard village list so the user can see where the bot is working right now.
    private bool _isActiveWorkingVillage;
    public bool IsActiveWorkingVillage
    {
        get => _isActiveWorkingVillage;
        set
        {
            if (_isActiveWorkingVillage == value)
            {
                return;
            }

            _isActiveWorkingVillage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string CoordsText => (CoordX.HasValue && CoordY.HasValue)
        ? $"({CoordX} | {CoordY})"
        : string.Empty;

    public string PopText
    {
        get
        {
            if (CropFields.HasValue && Population.HasValue)
            {
                return $"{CropFields.Value}c - {Population.Value}";
            }

            if (Population.HasValue)
            {
                return Population.Value.ToString();
            }

            if (CropFields.HasValue)
            {
                return $"{CropFields.Value}c";
            }

            return string.Empty;
        }
    }

    public string NameWithCoords
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Name))
            {
                parts.Add(Name);
            }

            if (!string.IsNullOrWhiteSpace(CoordsText))
            {
                parts.Add(CoordsText);
            }

            // Population/crop fields are intentionally omitted here: the village dropdown shows
            // only the village name and coordinates.
            return string.Join(" ", parts);
        }
    }

    public string CapitalText => IsCapital ? "(Capital)" : string.Empty;

    public string DisplayName => Name;

    public override string ToString() => DisplayName;
}
