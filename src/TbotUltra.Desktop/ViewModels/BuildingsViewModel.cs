using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Buildings panel. It owns the rendered building-slot
/// state and panel command enablement; <see cref="Services.BuildingsPanelService"/>
/// owns persisted queue access while MainWindow retains lifecycle and dialogs.
/// </summary>
public sealed class BuildingsViewModel : BaseViewModel
{
    private readonly RelayCommand _loadCommand;
    private readonly RelayCommand _upgradeAllToMaxCommand;
    private readonly RelayCommand _openQueueCommand;
    private readonly RelayCommand _templatesCommand;
    private readonly RelayCommand _showSlotsCommand;
    private readonly RelayCommand _demolishOverviewCommand;
    private readonly RelayCommand _stopDemolitionCommand;
    private readonly RelayCommand<BuildingSlotRow> _slotSelectedCommand;
    private readonly RelayCommand<BuildingSlotRow> _quickUpgradeCommand;
    private readonly Dictionary<int, DateTimeOffset> _slotClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _lastQueuedTargetBySlot = new();
    private readonly Dictionary<int, (string Name, int Gid, DateTimeOffset At)> _lastQueuedConstructBySlot = new();
    private string _demolishStatusText = "No demolition queued for this village.";
    private bool _demolishStatusHasTimer;
    private string _queueTimeText = "0h";
    private string _constructFasterQueueTimeText = "0h";

    public BuildingsViewModel()
    {
        _loadCommand = new RelayCommand(() => LoadRequested?.Invoke());
        _upgradeAllToMaxCommand = new RelayCommand(() => UpgradeAllToMaxRequested?.Invoke());
        _openQueueCommand = new RelayCommand(() => OpenQueueRequested?.Invoke());
        _templatesCommand = new RelayCommand(() => TemplatesRequested?.Invoke());
        _showSlotsCommand = new RelayCommand(() => ShowSlotsRequested?.Invoke());
        _demolishOverviewCommand = new RelayCommand(() => DemolishOverviewRequested?.Invoke());
        _stopDemolitionCommand = new RelayCommand(() => StopDemolitionRequested?.Invoke());
        _slotSelectedCommand = new RelayCommand<BuildingSlotRow>(row => SlotSelected?.Invoke(row));
        _quickUpgradeCommand = new RelayCommand<BuildingSlotRow>(
            row => QuickUpgradeRequested?.Invoke(row),
            row => row.CanQueueOneLevelUpgrade);
    }

    public ICommand LoadCommand => _loadCommand;
    public ICommand UpgradeAllToMaxCommand => _upgradeAllToMaxCommand;
    public ICommand OpenQueueCommand => _openQueueCommand;
    public ICommand TemplatesCommand => _templatesCommand;
    public ICommand ShowSlotsCommand => _showSlotsCommand;
    public ICommand DemolishOverviewCommand => _demolishOverviewCommand;
    public ICommand StopDemolitionCommand => _stopDemolitionCommand;
    public ICommand SlotSelectedCommand => _slotSelectedCommand;
    public ICommand QuickUpgradeCommand => _quickUpgradeCommand;

    public event Action? LoadRequested;
    public event Action? UpgradeAllToMaxRequested;
    public event Action? OpenQueueRequested;
    public event Action? TemplatesRequested;
    public event Action? ShowSlotsRequested;
    public event Action? DemolishOverviewRequested;
    public event Action? StopDemolitionRequested;
    public event Action<BuildingSlotRow>? SlotSelected;
    public event Action<BuildingSlotRow>? QuickUpgradeRequested;

    public string DemolishStatusText
    {
        get => _demolishStatusText;
        set => SetProperty(ref _demolishStatusText, value);
    }

    public bool DemolishStatusHasTimer
    {
        get => _demolishStatusHasTimer;
        set => SetProperty(ref _demolishStatusHasTimer, value);
    }

    public string QueueTimeText
    {
        get => _queueTimeText;
        private set => SetProperty(ref _queueTimeText, value);
    }

    public string ConstructFasterQueueTimeText
    {
        get => _constructFasterQueueTimeText;
        private set => SetProperty(ref _constructFasterQueueTimeText, value);
    }

    public void ApplyQueueDuration(string normalTime, string constructFasterTime)
    {
        QueueTimeText = normalTime;
        ConstructFasterQueueTimeText = constructFasterTime;
    }

    /// <summary>
    /// Building slots shown on the Buildings tab. Created once and mutated in
    /// place so the panel's CollectionViewSource bindings stay stable.
    /// </summary>
    public ObservableCollection<BuildingSlotRow> BuildingSlots { get; } = [];

    /// <summary>
    /// Occupied slots offered as demolish targets (bound to the demolish picker).
    /// </summary>
    public ObservableCollection<BuildingSlotRow> DemolishableBuildings { get; } = [];

    /// <summary>
    /// Buildings constructable in the active village (bound to the construct picker).
    /// </summary>
    public ObservableCollection<BuildingCatalogOption> BuildingCatalogOptions { get; } = [];

    public void ClearQueueInteractionState()
    {
        _slotClickCooldownBySlot.Clear();
        ClearQueuedItemState();
    }

    public void ClearQueuedItemState()
    {
        _lastQueuedTargetBySlot.Clear();
        _lastQueuedConstructBySlot.Clear();
    }

    public bool TryBeginSlotClick(int slotId, DateTimeOffset now)
    {
        if (_slotClickCooldownBySlot.TryGetValue(slotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return false;
        }

        _slotClickCooldownBySlot[slotId] = now;
        return true;
    }

    public bool WasUpgradeQueuedRecently(int slotId, int target, DateTimeOffset now)
    {
        return _lastQueuedTargetBySlot.TryGetValue(slotId, out var lastQueued)
            && lastQueued.Target == target
            && (now - lastQueued.At).TotalMilliseconds < 2500;
    }

    public void RememberQueuedUpgrade(int slotId, int target, DateTimeOffset now)
        => _lastQueuedTargetBySlot[slotId] = (target, now);

    public bool TryGetQueuedUpgrade(int slotId, out int target)
    {
        if (_lastQueuedTargetBySlot.TryGetValue(slotId, out var queued))
        {
            target = queued.Target;
            return true;
        }

        target = default;
        return false;
    }

    public bool WasConstructQueuedRecently(int slotId, string name, DateTimeOffset now)
    {
        return _lastQueuedConstructBySlot.TryGetValue(slotId, out var lastQueued)
            && string.Equals(lastQueued.Name, name, StringComparison.OrdinalIgnoreCase)
            && (now - lastQueued.At).TotalMilliseconds < 2500;
    }

    public void RememberQueuedConstruct(int slotId, string name, int gid, DateTimeOffset now)
        => _lastQueuedConstructBySlot[slotId] = (name, gid, now);

    public bool TryGetQueuedConstruct(int slotId, out string name, out int gid)
    {
        if (_lastQueuedConstructBySlot.TryGetValue(slotId, out var queued))
        {
            name = queued.Name;
            gid = queued.Gid;
            return true;
        }

        name = string.Empty;
        gid = default;
        return false;
    }

    public void ForgetQueuedUpgrade(int slotId) => _lastQueuedTargetBySlot.Remove(slotId);

    public void ForgetQueuedConstruct(int slotId) => _lastQueuedConstructBySlot.Remove(slotId);

    public void ForgetInactiveQueueItems(IReadOnlySet<int> activeUpgradeSlots, IReadOnlySet<int> activeConstructSlots)
    {
        foreach (var slotId in _lastQueuedTargetBySlot.Keys.Except(activeUpgradeSlots).ToList())
        {
            _lastQueuedTargetBySlot.Remove(slotId);
        }

        foreach (var slotId in _lastQueuedConstructBySlot.Keys.Except(activeConstructSlots).ToList())
        {
            _lastQueuedConstructBySlot.Remove(slotId);
        }
    }

    /// <summary>
    /// Slots pinned to the top row of the Buildings tab (Main Building, Rally Point, Wall).
    /// </summary>
    public static bool IsPinnedBuildingTopSlot(int slotId)
    {
        return slotId == 26 || slotId == 39 || slotId == 40;
    }

    public static readonly HashSet<int> WallGids = [31, 32, 33, 42, 43];

    public static bool IsRallyPointSlot(int slotId) => slotId == 39;

    public static bool IsRallyPointGid(int gid) => gid == 16;

    public static bool IsEmptyBuilding(Building building)
    {
        return (building.Gid ?? 0) <= 0
            && ((building.Level ?? 0) <= 0
                || string.IsNullOrWhiteSpace(building.Name)
                || string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A fixed special slot (Rally Point 39 / Wall 40) that exists from founding but has never
    /// been built: it carries its gid at level 0 and must count as free, not occupied.
    /// </summary>
    public static bool IsUnbuiltFixedSpecialBuilding(int slotId, Building building)
    {
        if ((building.Level ?? 0) > 0 || building.Gid is not int gid)
        {
            return false;
        }

        return (slotId == 39 && IsRallyPointGid(gid))
            || (slotId == 40 && WallGids.Contains(gid));
    }

    /// <summary>
    /// Occupied state and displayed name/level/gid for one building slot, exactly as the
    /// Buildings tab renders it: a slot with an identified building counts as occupied even
    /// at level 0/gid 0; unbuilt fixed specials show their type name at level 0; other empty
    /// slots show "Empty" with no level.
    /// </summary>
    public static (bool Occupied, string Name, int? Level, int? Gid) ResolveSlotIdentity(
        int slotId,
        Building? building,
        string tribe)
    {
        var isKnownEmpty = building is null || IsEmptyBuilding(building);
        var hasIdentifiedBuildingName = building is not null
            && !string.IsNullOrWhiteSpace(building.Name)
            && !string.Equals(building.Name, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase)
            && !building.Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
        var isUnbuiltFixedSpecial = building is not null && IsUnbuiltFixedSpecialBuilding(slotId, building);
        var occupied = building is not null
            && !isKnownEmpty
            && !isUnbuiltFixedSpecial
            && ((building.Level ?? 0) > 0
                || (building.Gid ?? 0) > 0
                || hasIdentifiedBuildingName);

        if (occupied)
        {
            return (true, building!.Name, building.Level, building.Gid);
        }

        if (slotId == 40 || IsRallyPointSlot(slotId))
        {
            var name = IsRallyPointSlot(slotId)
                ? "Rally Point"
                : BuildingCatalogService.WallForTribe(tribe)?.Name ?? "Wall";
            return (false, name, 0, null);
        }

        return (false, "Empty", null, null);
    }

    /// <summary>
    /// Computes the circular canvas layout (Left/Top per slot id) for every possible
    /// village/City building slot (ids 19–43). Pure geometry: no UI or service state.
    /// </summary>
    public static IReadOnlyDictionary<int, (double Left, double Top)> CreateBuildingSlotLayout()
    {
        const double canvasWidth = 760d;
        const double canvasHeight = 430d;
        const double slotCardWidth = 92d;
        const double centerX = (canvasWidth - slotCardWidth) / 2d;
        const double centerY = (canvasHeight - slotCardWidth) / 2d;
        const double radiusX = 300d;
        const double radiusY = 155d;

        var map = new Dictionary<int, (double Left, double Top)>();
        var slots = Enumerable.Range(19, 25).ToArray();
        for (var index = 0; index < slots.Length; index++)
        {
            var angle = (-Math.PI / 2d) + (2d * Math.PI * index / slots.Length);
            var left = centerX + (Math.Cos(angle) * radiusX);
            var top = centerY + (Math.Sin(angle) * radiusY);
            map[slots[index]] = (Math.Round(left, 1), Math.Round(top, 1));
        }

        return map;
    }

    /// <summary>
    /// Status line shown after a buildings load: how many slots are occupied vs free.
    /// <paramref name="villageDescriptor"/> is the caller's phrasing of which village
    /// was loaded (e.g. "active village 'Capital'").
    /// </summary>
    public string DescribeLoadedSlots(string villageDescriptor)
    {
        var occupied = BuildingSlots.Count(row => row.IsOccupied);
        var free = BuildingSlots.Count(row => !row.IsOccupied);
        return $"Buildings loaded for {villageDescriptor}. Occupied slots: {occupied}, free slots: {free}.";
    }
}
