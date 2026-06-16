using System.Collections.ObjectModel;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.ViewModels;

public sealed class TroopTrainingQuickVillageRow
{
    public TroopTrainingQuickVillageRow(
        string villageKey,
        string villageName,
        TroopTrainingPayload basePayload,
        string? tribe)
    {
        VillageKey = villageKey;
        VillageName = string.IsNullOrWhiteSpace(villageName) ? villageKey : villageName.Trim();
        BasePayload = basePayload;

        // Each training building only trains its own troops, so resolve the dropdown per building
        // (the Barracks list must not show Stable/Workshop units) — same source the Troops tab uses.
        Barracks = CreateCell(TroopTrainingBuildingType.Barracks, "Barracks", basePayload, tribe);
        Stable = CreateCell(TroopTrainingBuildingType.Stable, "Stable", basePayload, tribe);
        Workshop = CreateCell(TroopTrainingBuildingType.Workshop, "Workshop", basePayload, tribe);
    }

    public string VillageKey { get; }
    public string VillageName { get; }
    public TroopTrainingPayload BasePayload { get; }
    public TroopTrainingQuickBuildingCell Barracks { get; }
    public TroopTrainingQuickBuildingCell Stable { get; }
    public TroopTrainingQuickBuildingCell Workshop { get; }

    public TroopTrainingPayload BuildPayload()
    {
        return TroopTrainingQuickSettings.ApplySelections(
            BasePayload,
            new[]
            {
                Barracks.ToSelection(),
                Stable.ToSelection(),
                Workshop.ToSelection(),
            });
    }

    private static TroopTrainingQuickBuildingCell CreateCell(
        TroopTrainingBuildingType buildingType,
        string title,
        TroopTrainingPayload payload,
        string? tribe)
    {
        var building = TroopTrainingQuickSettings.BuildingPayloadFor(payload, buildingType);
        var troopOptions = TroopCatalog.ResolveTroopTypesForTribe(tribe, buildingType);
        return new TroopTrainingQuickBuildingCell(
            buildingType,
            title,
            building.Enabled,
            building.TroopType,
            troopOptions);
    }
}

public sealed class TroopTrainingQuickBuildingCell : BaseViewModel
{
    private bool _isEnabled;
    private string _selectedTroop;

    public TroopTrainingQuickBuildingCell(
        TroopTrainingBuildingType buildingType,
        string title,
        bool isEnabled,
        string selectedTroop,
        IReadOnlyList<string> troopOptions)
    {
        BuildingType = buildingType;
        Title = title;
        _isEnabled = isEnabled;
        _selectedTroop = selectedTroop?.Trim() ?? string.Empty;
        TroopOptions = new ObservableCollection<string>(troopOptions);
        if (TroopOptions.Count > 0
            && !TroopOptions.Any(item => string.Equals(item, _selectedTroop, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedTroop = TroopOptions[0];
        }
    }

    public TroopTrainingBuildingType BuildingType { get; }
    public string Title { get; }
    public ObservableCollection<string> TroopOptions { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string SelectedTroop
    {
        get => _selectedTroop;
        set => SetProperty(ref _selectedTroop, value?.Trim() ?? string.Empty);
    }

    public TroopTrainingQuickBuildingSelection ToSelection()
    {
        return new TroopTrainingQuickBuildingSelection(BuildingType, IsEnabled, SelectedTroop);
    }
}
