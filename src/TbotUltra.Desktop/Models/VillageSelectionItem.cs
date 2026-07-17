using System.Collections.Generic;
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

    // Dashboard overview indicators, filled from the per-village status cache. Build slots reflect the
    // construction queue (2 normally, 3 for Romans); troop slots are Barracks/Stable/Workshop. Settable
    // so they can be refreshed in place when a village is rescanned without rebuilding the whole list.
    private IReadOnlyList<VillageActivitySlot> _buildingSlots = System.Array.Empty<VillageActivitySlot>();
    public IReadOnlyList<VillageActivitySlot> BuildingSlots
    {
        get => _buildingSlots;
        set
        {
            _buildingSlots = value ?? System.Array.Empty<VillageActivitySlot>();
            OnPropertyChanged();
        }
    }

    private IReadOnlyList<VillageActivitySlot> _troopSlots = System.Array.Empty<VillageActivitySlot>();
    public IReadOnlyList<VillageActivitySlot> TroopSlots
    {
        get => _troopSlots;
        set
        {
            _troopSlots = value ?? System.Array.Empty<VillageActivitySlot>();
            OnPropertyChanged();
        }
    }

    // Smithy upgrade slots (2 simultaneous), driven by the village's live SmithyUpgradeStatus queue.
    private IReadOnlyList<VillageActivitySlot> _smithySlots = System.Array.Empty<VillageActivitySlot>();
    public IReadOnlyList<VillageActivitySlot> SmithySlots
    {
        get => _smithySlots;
        set
        {
            _smithySlots = value ?? System.Array.Empty<VillageActivitySlot>();
            OnPropertyChanged();
        }
    }

    // Whether this village has construction queued (green queue icon) or not (muted). Lets the user see at
    // a glance which villages need more queued.
    private bool _hasQueue;
    public bool HasQueue
    {
        get => _hasQueue;
        set
        {
            if (_hasQueue == value)
            {
                return;
            }

            _hasQueue = value;
            OnPropertyChanged();
        }
    }

    private string _queueTooltip = "No construction queued here — consider queuing more";
    public string QueueTooltip
    {
        get => _queueTooltip;
        set
        {
            var tooltip = string.IsNullOrWhiteSpace(value)
                ? "No construction queued here — consider queuing more"
                : value;
            if (string.Equals(_queueTooltip, tooltip, System.StringComparison.Ordinal))
            {
                return;
            }

            _queueTooltip = tooltip;
            OnPropertyChanged();
        }
    }

    // True only for the hero's home village AND when the hero is currently away (adventure/attack/etc).
    // Combined with IsHeroHome this gives the icon three states: dark (not hero's village), green (home),
    // yellow (home village but hero is away).
    private bool _isHeroAway;
    public bool IsHeroAway
    {
        get => _isHeroAway;
        set
        {
            if (_isHeroAway == value)
            {
                return;
            }

            _isHeroAway = value;
            OnPropertyChanged();
        }
    }

    // True only for the hero's home village AND when the hero is reviving. Drives the orange hero icon
    // (between away/yellow and dead/red).
    private bool _isHeroReviving;
    public bool IsHeroReviving
    {
        get => _isHeroReviving;
        set
        {
            if (_isHeroReviving == value)
            {
                return;
            }

            _isHeroReviving = value;
            OnPropertyChanged();
        }
    }

    // True only for the hero's home village AND when the hero is dead. Drives the red hero icon (overrides
    // the green/yellow states).
    private bool _isHeroDead;
    public bool IsHeroDead
    {
        get => _isHeroDead;
        set
        {
            if (_isHeroDead == value)
            {
                return;
            }

            _isHeroDead = value;
            OnPropertyChanged();
        }
    }

    // True only for the village that is the hero's home village (one at a time). Drives the bright vs
    // dark hero icon in the village list.
    private bool _isHeroHome;
    public bool IsHeroHome
    {
        get => _isHeroHome;
        set
        {
            if (_isHeroHome == value)
            {
                return;
            }

            _isHeroHome = value;
            OnPropertyChanged();
        }
    }

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
