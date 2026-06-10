using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.ViewModels;

public sealed class MapOasisViewModel : BaseViewModel
{
    private TravcoListStore.TravcoSavedList? _selectedSavedList;
    private string _statusText = "Choose oasis types and start the analysis.";

    public ObservableCollection<TravcoListStore.TravcoSavedList> SavedLists { get; } = [];
    public ObservableCollection<OasisListRow> Rows { get; } = [];

    public TravcoListStore.TravcoSavedList? SelectedSavedList
    {
        get => _selectedSavedList;
        set => SetProperty(ref _selectedSavedList, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
}
