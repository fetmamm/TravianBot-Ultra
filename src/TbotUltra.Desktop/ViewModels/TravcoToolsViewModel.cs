using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.ViewModels;

public sealed class TravcoToolsViewModel : BaseViewModel
{
    private TravcoListStore.TravcoSavedList? _selectedSavedList;
    private VillageSelectionItem? _selectedVillage;
    private string _daysInactiveText = "2";
    private string _selectedOrderBy = "distance";
    private string _listName = "Travco page 1";
    private string _statusText = "Run an inactive search or open a saved list.";

    public ObservableCollection<TravcoListStore.TravcoSavedList> SavedLists { get; } = [];
    public ObservableCollection<TravcoListRow> Rows { get; } = [];
    public ObservableCollection<VillageSelectionItem> Villages { get; } = [];
    public ObservableCollection<TravcoOrderByOption> OrderByOptions { get; } =
    [
        new("Distance", "distance"),
        new("Population", "-population"),
        new("Tribe", "tid"),
    ];

    public TravcoListStore.TravcoSavedList? SelectedSavedList
    {
        get => _selectedSavedList;
        set => SetProperty(ref _selectedSavedList, value);
    }

    public string ListName
    {
        get => _listName;
        set => SetProperty(ref _listName, value);
    }

    public VillageSelectionItem? SelectedVillage
    {
        get => _selectedVillage;
        set => SetProperty(ref _selectedVillage, value);
    }

    public string DaysInactiveText
    {
        get => _daysInactiveText;
        set => SetProperty(ref _daysInactiveText, value);
    }

    public string SelectedOrderBy
    {
        get => _selectedOrderBy;
        set => SetProperty(ref _selectedOrderBy, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public sealed record TravcoOrderByOption(string Label, string Value);
}
