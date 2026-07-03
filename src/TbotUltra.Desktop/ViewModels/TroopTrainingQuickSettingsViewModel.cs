using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.ViewModels;

public sealed class TroopTrainingQuickVillageRow : BaseViewModel
{
    private bool _isBuildTroopsEnabled;
    private bool _isExpanded;
    private bool _checkWood;
    private bool _checkClay;
    private bool _checkIron;
    private bool _checkCrop;
    private int _fallbackCooldownSeconds;

    public TroopTrainingQuickVillageRow(
        string villageKey,
        string villageName,
        bool isBuildTroopsEnabled,
        TroopTrainingPayload basePayload,
        string? tribe)
    {
        VillageKey = villageKey;
        VillageName = string.IsNullOrWhiteSpace(villageName) ? villageKey : villageName.Trim();
        _isBuildTroopsEnabled = isBuildTroopsEnabled;
        BasePayload = basePayload;

        // Each training building only trains its own troops, so resolve the dropdown per building
        // (the Barracks list must not show Stable/Workshop units) — same source the Troops tab uses.
        Barracks = CreateCell(TroopTrainingBuildingType.Barracks, "Barracks", basePayload, tribe);
        Stable = CreateCell(TroopTrainingBuildingType.Stable, "Stable", basePayload, tribe);
        Workshop = CreateCell(TroopTrainingBuildingType.Workshop, "Workshop", basePayload, tribe);
        BuildingCells = [Barracks, Stable, Workshop];

        // The Troops tab shows one Wood/Clay/Iron/Crop set shared by all buildings; the payload
        // stores the same flags per building, so read them from Barracks (same as the Troops tab).
        _checkWood = basePayload.Barracks.CheckWood;
        _checkClay = basePayload.Barracks.CheckClay;
        _checkIron = basePayload.Barracks.CheckIron;
        _checkCrop = basePayload.Barracks.CheckCrop;
        _fallbackCooldownSeconds = NormalizeFallbackCooldown(basePayload.FallbackCooldownSeconds);
    }

    public string VillageKey { get; }
    public string VillageName { get; }
    public TroopTrainingPayload BasePayload { get; }
    public TroopTrainingBuildingOption Barracks { get; }
    public TroopTrainingBuildingOption Stable { get; }
    public TroopTrainingBuildingOption Workshop { get; }

    /// <summary>The three cells in display order, for the expanded detail editor.</summary>
    public IReadOnlyList<TroopTrainingBuildingOption> BuildingCells { get; }

    public bool IsBuildTroopsEnabled
    {
        get => _isBuildTroopsEnabled;
        set => SetProperty(ref _isBuildTroopsEnabled, value);
    }

    /// <summary>Whether the row's full settings editor is shown below the compact row.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool CheckWood
    {
        get => _checkWood;
        set => SetProperty(ref _checkWood, value);
    }

    public bool CheckClay
    {
        get => _checkClay;
        set => SetProperty(ref _checkClay, value);
    }

    public bool CheckIron
    {
        get => _checkIron;
        set => SetProperty(ref _checkIron, value);
    }

    public bool CheckCrop
    {
        get => _checkCrop;
        set => SetProperty(ref _checkCrop, value);
    }

    public int FallbackCooldownSeconds
    {
        get => _fallbackCooldownSeconds;
        set => SetProperty(ref _fallbackCooldownSeconds, NormalizeFallbackCooldown(value));
    }

    public TroopTrainingPayload BuildPayload()
    {
        return BasePayload with
        {
            Barracks = ApplyCell(BasePayload.Barracks, Barracks),
            Stable = ApplyCell(BasePayload.Stable, Stable),
            Workshop = ApplyCell(BasePayload.Workshop, Workshop),
            FallbackCooldownSeconds = FallbackCooldownSeconds,
        };
    }

    private TroopTrainingBuildingPayload ApplyCell(
        TroopTrainingBuildingPayload source,
        TroopTrainingBuildingOption cell)
    {
        // MinimumTroops is intentionally preserved from the source payload — it has no UI on
        // either the Troops tab or this popup.
        return source with
        {
            Enabled = cell.IsEnabled,
            TroopType = cell.SelectedTroop,
            MaxQueueHours = cell.MaxQueueMode,
            AmountMode = cell.AmountMode,
            KeepResourcesPercent = cell.KeepResourcesPercent,
            RunMode = cell.RunMode,
            MinimumResourcesPercent = cell.MinimumResourcesPercent,
            TimedMinMinutes = cell.TimedMinMinutes,
            TimedMaxMinutes = cell.TimedMaxMinutes,
            CheckWood = CheckWood,
            CheckClay = CheckClay,
            CheckIron = CheckIron,
            CheckCrop = CheckCrop,
        };
    }

    private static TroopTrainingBuildingOption CreateCell(
        TroopTrainingBuildingType buildingType,
        string title,
        TroopTrainingPayload payload,
        string? tribe)
    {
        var building = TroopTrainingQuickSettings.BuildingPayloadFor(payload, buildingType);
        var troopOptions = TroopCatalog.ResolveTroopTypesForTribe(tribe, buildingType);

        // Reuse the Troops tab's row model so the popup gets the same normalization
        // (min<=max timed clamp, mode helper properties) and binding surface for free.
        var cell = new TroopTrainingBuildingOption { BuildingType = buildingType, Title = title };
        foreach (var troop in troopOptions)
        {
            cell.TroopOptions.Add(troop);
        }

        cell.IsEnabled = building.Enabled;
        cell.SelectedTroop = building.TroopType;
        if (cell.TroopOptions.Count > 0
            && !cell.TroopOptions.Any(item => string.Equals(item, cell.SelectedTroop, StringComparison.OrdinalIgnoreCase)))
        {
            cell.SelectedTroop = cell.TroopOptions[0];
        }

        cell.MaxQueueMode = building.MaxQueueHours;
        cell.AmountMode = building.AmountMode;
        cell.KeepResourcesPercent = building.KeepResourcesPercent;
        cell.RunMode = building.RunMode;
        cell.MinimumTroops = building.MinimumTroops;
        cell.MinimumResourcesPercent = building.MinimumResourcesPercent;
        cell.TimedMinMinutes = building.TimedMinMinutes;
        cell.TimedMaxMinutes = building.TimedMaxMinutes;
        return cell;
    }

    // Same allowed set as the Troops tab's Fallback wait combo (TroopTrainingViewModel).
    private static int NormalizeFallbackCooldown(int value) => value switch
    {
        10 or 30 or 60 or 120 or 300 or 600 => value,
        _ => 30,
    };
}
