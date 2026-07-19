using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

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
