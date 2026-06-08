using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.ViewModels;

public sealed class TravcoToolsViewModel : BaseViewModel
{
    private TravcoListStore.TravcoSavedList? _selectedSavedList;
    private string _listName = "Travco page 1";
    private string _statusText = "Run an inactive search or open a saved list.";

    public ObservableCollection<TravcoListStore.TravcoSavedList> SavedLists { get; } = [];
    public ObservableCollection<TravcoListRow> Rows { get; } = [];

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

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
}
