using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Buildings panel. First MVVM slice for that panel:
/// it owns the building-slot collection that the panel renders. The service-
/// and queue-bound mutation logic still lives in MainWindow code-behind and
/// mutates this collection in place; it will migrate here in later steps.
/// </summary>
public sealed class BuildingsViewModel : BaseViewModel
{
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

    /// <summary>
    /// Slots pinned to the top row of the Buildings tab (Main Building, Rally Point, Wall).
    /// </summary>
    public static bool IsPinnedBuildingTopSlot(int slotId)
    {
        return slotId == 26 || slotId == 39 || slotId == 40;
    }

    /// <summary>
    /// Computes the circular canvas layout (Left/Top per slot id) for the 22
    /// village building slots (ids 19–40). Pure geometry: no UI or service state.
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
        var slots = Enumerable.Range(19, 22).ToArray();
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
